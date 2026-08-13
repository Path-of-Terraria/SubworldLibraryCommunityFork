using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.Social;
using Terraria.Utilities;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldSystem
	{
		private static readonly Dictionary<string, Vector2> returnPositions = new Dictionary<string, Vector2>();
		private static Vector2? pendingReturnPosition;
		private static string pendingReturnPositionId;

		internal static void CaptureReturnPosition()
		{
			if (Main.netMode == NetmodeID.Server || current?.ReturnToPreviousPosition != true || Main.myPlayer < 0 || Main.myPlayer >= 255)
			{
				return;
			}

			Player player = Main.LocalPlayer;
			if (player == null || !player.active)
			{
				return;
			}

			returnPositions[current.FullName] = player.position;
		}

		internal static void PrepareReturnPosition(Subworld destination, bool destinationIsRunning)
		{
			pendingReturnPosition = null;
			pendingReturnPositionId = null;

			if (destination?.ReturnToPreviousPosition != true)
			{
				return;
			}

			if (!destinationIsRunning)
			{
				returnPositions.Remove(destination.FullName);
				return;
			}

			if (returnPositions.TryGetValue(destination.FullName, out Vector2 position))
			{
				pendingReturnPosition = position;
				pendingReturnPositionId = destination.FullName;
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
			if (!pendingReturnPosition.HasValue || current?.ReturnToPreviousPosition != true || Main.netMode == NetmodeID.Server || player.whoAmI != Main.myPlayer)
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
		/// Discards the remembered position for the specified subworld in the local game session.
		/// </summary>
		/// <param name="id">The subworld's full content name, such as <c>MyMod/MySubworld</c>.</param>
		public static void ClearReturnPosition(string id)
		{
			returnPositions.Remove(id);
			if (pendingReturnPositionId == id)
			{
				pendingReturnPosition = null;
				pendingReturnPositionId = null;
			}
		}

		/// <summary>
		/// Discards the remembered position for the specified subworld type in the local game session.
		/// </summary>
		public static void ClearReturnPosition<T>() where T : Subworld
		{
			ClearReturnPosition(ModContent.GetInstance<T>().FullName);
		}

		private static void ClearReturnPositions()
		{
			returnPositions.Clear();
			pendingReturnPosition = null;
			pendingReturnPositionId = null;
		}
	}
}
