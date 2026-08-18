using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.Social;
using Terraria.Utilities;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldSystem
	{
		private const string PlayerReturnPositionsKey = "returnPositions";
		private const string WorldReturnInstancesKey = "returnInstances";

		private readonly struct ReturnPosition
		{
			public readonly Vector2 Position;
			public readonly Guid InstanceId;

			public ReturnPosition(Vector2 position, Guid instanceId)
			{
				Position = position;
				InstanceId = instanceId;
			}
		}

		// Client-local positions also act as the "has left this instance" marker in shared mode,
		// so first-time entrants continue to use the subworld's normal spawn.
		private static readonly Dictionary<string, ReturnPosition> returnPositions = new Dictionary<string, ReturnPosition>();
		// The main server owns shared positions; single-player uses the same store locally.
		private static readonly Dictionary<string, ReturnPosition> sharedReturnPositions = new Dictionary<string, ReturnPosition>();
		private static readonly Dictionary<string, Guid> returnInstanceIds = new Dictionary<string, Guid>();
		private static Vector2? pendingReturnPosition;
		private static string pendingReturnPositionId;
		private static Guid currentReturnInstanceId;

		internal static void SavePlayerReturnPositions(TagCompound tag)
		{
			List<TagCompound> entries = new List<TagCompound>();
			foreach (KeyValuePair<string, ReturnPosition> pair in returnPositions)
			{
				Subworld subworld = FindReturnSubworld(pair.Key);
				if (subworld == null || subworld.ReturnPositionMode == SubworldReturnPositionMode.Disabled
					|| pair.Value.InstanceId == Guid.Empty || !IsFinite(pair.Value.Position))
				{
					continue;
				}

				entries.Add(new TagCompound
				{
					["id"] = pair.Key,
					["instanceId"] = pair.Value.InstanceId.ToByteArray(),
					["x"] = pair.Value.Position.X,
					["y"] = pair.Value.Position.Y
				});
			}

			if (entries.Count > 0)
			{
				tag[PlayerReturnPositionsKey] = entries;
			}
		}

		internal static void LoadPlayerReturnPositions(TagCompound tag)
		{
			returnPositions.Clear();
			if (!tag.ContainsKey(PlayerReturnPositionsKey))
			{
				return;
			}

			IList<TagCompound> entries = tag.GetList<TagCompound>(PlayerReturnPositionsKey);
			foreach (TagCompound entry in entries)
			{
				if (!TryReadReturnPosition(entry, out string id, out ReturnPosition returnPosition))
				{
					continue;
				}

				returnPositions[id] = returnPosition;
			}
		}

		private static void SaveReturnWorldState(TagCompound tag)
		{
			if (IsMainWorldDataContext())
			{
				WriteReturnWorldState(tag);
			}
		}

		private static void LoadReturnWorldState(TagCompound tag)
		{
			// Returning to the main world happens within the same process. Keep the newer
			// in-memory state instead of replacing it with the main world's previous save.
			if (cache == null && IsMainWorldDataContext())
			{
				ReadReturnWorldState(tag);
			}
		}

		private static void WriteReturnWorldState(TagCompound tag)
		{
			List<TagCompound> entries = new List<TagCompound>();
			foreach (KeyValuePair<string, Guid> pair in returnInstanceIds)
			{
				Subworld subworld = FindReturnSubworld(pair.Key);
				if (subworld == null || subworld.ReturnPositionMode == SubworldReturnPositionMode.Disabled
					|| pair.Value == Guid.Empty)
				{
					continue;
				}

				TagCompound entry = new TagCompound
				{
					["id"] = pair.Key,
					["instanceId"] = pair.Value.ToByteArray()
				};

				if (subworld.ReturnPositionMode == SubworldReturnPositionMode.Shared
					&& sharedReturnPositions.TryGetValue(pair.Key, out ReturnPosition sharedPosition)
					&& sharedPosition.InstanceId == pair.Value && IsFinite(sharedPosition.Position))
				{
					entry["sharedX"] = sharedPosition.Position.X;
					entry["sharedY"] = sharedPosition.Position.Y;
				}

				entries.Add(entry);
			}

			if (entries.Count > 0)
			{
				tag[WorldReturnInstancesKey] = entries;
			}
		}

		private static void ReadReturnWorldState(TagCompound tag)
		{
			sharedReturnPositions.Clear();
			returnInstanceIds.Clear();

			if (!tag.ContainsKey(WorldReturnInstancesKey))
			{
				return;
			}

			IList<TagCompound> entries = tag.GetList<TagCompound>(WorldReturnInstancesKey);
			foreach (TagCompound entry in entries)
			{
				if (!TryReadInstanceId(entry, out string id, out Guid instanceId))
				{
					continue;
				}

				// World data may also load before subworld ModTypes are registered. The
				// destination-specific accessors enforce opt-in before using this state.
				returnInstanceIds[id] = instanceId;
				if (entry.TryGet("sharedX", out float sharedX) && entry.TryGet("sharedY", out float sharedY))
				{
					Vector2 position = new Vector2(sharedX, sharedY);
					if (IsFinite(position))
					{
						sharedReturnPositions[id] = new ReturnPosition(position, instanceId);
					}
				}
			}
		}

		private static bool TryReadReturnPosition(TagCompound tag, out string id, out ReturnPosition returnPosition)
		{
			returnPosition = default;
			if (!TryReadInstanceId(tag, out id, out Guid instanceId)
				|| !tag.TryGet("x", out float x) || !tag.TryGet("y", out float y))
			{
				return false;
			}

			Vector2 position = new Vector2(x, y);
			if (!IsFinite(position))
			{
				return false;
			}

			returnPosition = new ReturnPosition(position, instanceId);
			return true;
		}

		private static bool TryReadInstanceId(TagCompound tag, out string id, out Guid instanceId)
		{
			id = null;
			instanceId = Guid.Empty;
			if (!tag.TryGet("id", out id) || string.IsNullOrWhiteSpace(id)
				|| !tag.TryGet("instanceId", out byte[] bytes) || bytes?.Length != 16)
			{
				return false;
			}

			instanceId = new Guid(bytes);
			return instanceId != Guid.Empty;
		}

		private static Subworld FindReturnSubworld(string id)
		{
			if (subworlds != null)
			{
				foreach (Subworld subworld in subworlds)
				{
					if (subworld.FullName == id)
					{
						return subworld;
					}
				}
			}

			return null;
		}

		private static bool IsMainWorldDataContext()
		{
			if (main == null)
			{
				return current == null;
			}

			return Main.ActiveWorldFileData?.Path == main.Path;
		}

		private static bool IsFinite(Vector2 position)
		{
			return float.IsFinite(position.X) && float.IsFinite(position.Y);
		}

		internal static void CaptureReturnPosition()
		{
			if (Main.netMode == NetmodeID.Server || current == null
				|| current.ReturnPositionMode == SubworldReturnPositionMode.Disabled
				|| currentReturnInstanceId == Guid.Empty
				|| Main.myPlayer < 0 || Main.myPlayer >= 255)
			{
				return;
			}

			Player player = Main.LocalPlayer;
			if (player == null || !player.active)
			{
				return;
			}

			Vector2 position = current.GetReturnPosition(player);
			if (!IsFinite(position))
			{
				return;
			}

			ReturnPosition returnPosition = new ReturnPosition(position, currentReturnInstanceId);
			returnPositions[current.FullName] = returnPosition;

			if (Main.netMode == NetmodeID.SinglePlayer && current.ReturnPositionMode == SubworldReturnPositionMode.Shared)
			{
				sharedReturnPositions[current.FullName] = returnPosition;
			}
		}

		internal static void WriteSharedReturnPositionRequest(BinaryWriter writer)
		{
			ReturnPosition returnPosition = default;
			bool hasSharedPosition = Main.netMode == NetmodeID.MultiplayerClient
				&& current?.ReturnPositionMode == SubworldReturnPositionMode.Shared
				&& returnPositions.TryGetValue(current.FullName, out returnPosition)
				&& returnPosition.InstanceId == currentReturnInstanceId;

			writer.Write(hasSharedPosition);
			if (!hasSharedPosition)
			{
				return;
			}

			writer.Write(returnPosition.InstanceId.ToByteArray());
			writer.Write(returnPosition.Position.X);
			writer.Write(returnPosition.Position.Y);
		}

		internal static void ReadSharedReturnPositionRequest(int player, BinaryReader reader)
		{
			if (!reader.ReadBoolean())
			{
				return;
			}

			Guid instanceId = new Guid(reader.ReadBytes(16));
			Vector2 position = new Vector2(reader.ReadSingle(), reader.ReadSingle());

			if (player < 0 || player >= playerLocations.Length || !float.IsFinite(position.X) || !float.IsFinite(position.Y))
			{
				return;
			}

			int sourceIndex = playerLocations[player];
			if (sourceIndex < 0 || sourceIndex >= subworlds.Count)
			{
				return;
			}

			Subworld source = subworlds[sourceIndex];
			if (source.ReturnPositionMode != SubworldReturnPositionMode.Shared
				|| !returnInstanceIds.TryGetValue(source.FullName, out Guid activeInstanceId)
				|| instanceId != activeInstanceId)
			{
				return;
			}

			Player sourcePlayer = Main.player[player];
			float maxX = Math.Max(0f, source.Width * 16f - sourcePlayer.width);
			float maxY = Math.Max(0f, source.Height * 16f - sourcePlayer.height);
			position = Vector2.Clamp(position, Vector2.Zero, new Vector2(maxX, maxY));
			sharedReturnPositions[source.FullName] = new ReturnPosition(position, instanceId);
		}

		internal static Guid GetMultiplayerReturnInstanceId(Subworld destination, bool destinationIsRunning)
		{
			if (destination == null || destination.ReturnPositionMode == SubworldReturnPositionMode.Disabled)
			{
				return Guid.Empty;
			}

			// An unsaved subworld is regenerated whenever its empty subserver exits. Saved
			// subworlds retain their instance ID across that process restart until a consumer
			// explicitly invalidates the instance before deleting or replacing its save.
			if (!destinationIsRunning && !destination.ShouldSave)
			{
				RotateReturnInstance(destination.FullName);
			}

			return GetOrCreateReturnInstanceId(destination.FullName);
		}

		internal static Guid GetSinglePlayerReturnInstanceId(Subworld destination)
		{
			if (destination == null || destination.ReturnPositionMode == SubworldReturnPositionMode.Disabled)
			{
				return Guid.Empty;
			}

			if (!IsSinglePlayerDestinationAvailable(destination))
			{
				RotateReturnInstance(destination.FullName);
			}

			return GetOrCreateReturnInstanceId(destination.FullName);
		}

		private static Guid GetOrCreateReturnInstanceId(string id)
		{
			if (!returnInstanceIds.TryGetValue(id, out Guid instanceId))
			{
				instanceId = Guid.NewGuid();
				returnInstanceIds[id] = instanceId;
			}

			return instanceId;
		}

		private static void RotateReturnInstance(string id)
		{
			ClearReturnPosition(id);
			returnInstanceIds[id] = Guid.NewGuid();
		}

		internal static Vector2? GetSharedReturnPosition(Subworld destination, Guid destinationInstanceId)
		{
			if (destination?.ReturnPositionMode != SubworldReturnPositionMode.Shared
				|| !sharedReturnPositions.TryGetValue(destination.FullName, out ReturnPosition returnPosition)
				|| returnPosition.InstanceId != destinationInstanceId)
			{
				return null;
			}

			return returnPosition.Position;
		}

		internal static void PrepareReturnPosition(Subworld destination, Guid destinationInstanceId, Vector2? sharedReturnPosition)
		{
			pendingReturnPosition = null;
			pendingReturnPositionId = null;
			currentReturnInstanceId = destinationInstanceId;

			if (destination == null || destination.ReturnPositionMode == SubworldReturnPositionMode.Disabled)
			{
				return;
			}

			if (destinationInstanceId == Guid.Empty)
			{
				returnPositions.Remove(destination.FullName);
				return;
			}

			if (returnPositions.TryGetValue(destination.FullName, out ReturnPosition returnPosition))
			{
				if (returnPosition.InstanceId == destinationInstanceId)
				{
					Vector2? position = destination.ReturnPositionMode == SubworldReturnPositionMode.Shared
						? sharedReturnPosition
						: returnPosition.Position;

					if (position.HasValue)
					{
						pendingReturnPosition = position.Value;
						pendingReturnPositionId = destination.FullName;
					}
				}
				else
				{
					returnPositions.Remove(destination.FullName);
				}
			}
		}

		private static bool IsSinglePlayerDestinationAvailable(Subworld destination)
		{
			if (destination == null || !destination.ShouldSave || main == null)
			{
				return false;
			}

			string worldDirectory = main.IsCloudSave ? Main.CloudWorldPath : Main.WorldPath;
			string path = Path.Combine(worldDirectory, main.UniqueId.ToString(), destination.FileName + ".wld");
			bool cloud = main.IsCloudSave && SocialAPI.Cloud != null;
			return FileUtilities.Exists(path, cloud);
		}

		private static void TryRestoreReturnPosition(Player player)
		{
			if (!pendingReturnPosition.HasValue || current == null
				|| current.ReturnPositionMode == SubworldReturnPositionMode.Disabled
				|| Main.netMode == NetmodeID.Server || player.whoAmI != Main.myPlayer)
			{
				return;
			}

			Vector2 position = pendingReturnPosition.Value;
			pendingReturnPosition = null;
			pendingReturnPositionId = null;

			float maxX = System.Math.Max(0f, Main.maxTilesX * 16f - player.width);
			float maxY = System.Math.Max(0f, Main.maxTilesY * 16f - player.height);
			player.position = Vector2.Clamp(position, Vector2.Zero, new Vector2(maxX, maxY));
			player.oldPosition = player.position;
			player.velocity = Vector2.Zero;
			player.fallStart = (int)(player.Bottom.Y / 16f);
			player.fallStart2 = player.fallStart;

			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				NetMessage.SendData(MessageID.PlayerControls, -1, -1, null, player.whoAmI);
			}
		}

		/// <summary>
		/// Discards the remembered position for the specified subworld. The removal is
		/// persisted the next time the player and main world are saved.
		/// </summary>
		/// <param name="id">The subworld's full content name, such as <c>MyMod/MySubworld</c>.</param>
		public static void ClearReturnPosition(string id)
		{
			returnPositions.Remove(id);
			sharedReturnPositions.Remove(id);
			if (pendingReturnPositionId == id)
			{
				pendingReturnPosition = null;
				pendingReturnPositionId = null;
			}
		}

		/// <summary>
		/// Discards the remembered position for the specified subworld type.
		/// </summary>
		public static void ClearReturnPosition<T>() where T : Subworld
		{
			ClearReturnPosition(ModContent.GetInstance<T>().FullName);
		}

		/// <summary>
		/// Invalidates the active instance identity for the specified subworld and discards
		/// its remembered local return position. Call this before deleting or replacing a
		/// saved subworld instance.
		/// </summary>
		/// <param name="id">The subworld's full content name, such as <c>MyMod/MySubworld</c>.</param>
		public static void InvalidateReturnInstance(string id)
		{
			RotateReturnInstance(id);

			if (current?.FullName == id)
			{
				currentReturnInstanceId = returnInstanceIds[id];
			}
		}

		/// <summary>
		/// Invalidates the active instance identity for the specified subworld type.
		/// </summary>
		public static void InvalidateReturnInstance<T>() where T : Subworld
		{
			InvalidateReturnInstance(ModContent.GetInstance<T>().FullName);
		}

		private static void ClearReturnPositions(bool clearPlayerPositions = true)
		{
			if (clearPlayerPositions)
			{
				returnPositions.Clear();
			}
			sharedReturnPositions.Clear();
			returnInstanceIds.Clear();
			pendingReturnPosition = null;
			pendingReturnPositionId = null;
			currentReturnInstanceId = Guid.Empty;
		}
	}
}
