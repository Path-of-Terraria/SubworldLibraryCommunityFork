using Microsoft.Xna.Framework;
using System;
using System.Diagnostics;
using System.Threading;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace SubworldLibraryCommunityFork
{
	public partial class SubworldSystem
	{
		internal static void MovePlayerToSubserver(int player, ushort id)
		{
			if (pendingMoves[player] >= 0)
			{
				return;
			}

			pendingMoves[player] = id;

			// a new move supersedes any handshake we were still waiting on
			ClearRejoinTracking(player);

			ModPacket packet = ModContent.GetInstance<SubworldLibrary>().GetPacket();
			packet.Write(id);
			packet.Send(player);

			if (playerLocations[player] >= 0)
			{
				subworlds[playerLocations[player]].link?.Send(GetDisconnectPacket(player, ModContent.GetInstance<SubworldLibrary>().NetID));
			}

			if (id != ushort.MaxValue)
			{
				// this respects the vanilla call order

				Main.player[player].active = false;
			NetMessage.SendData(MessageID.PlayerActive, -1, player, null, player, 0);
				ChatHelper.BroadcastChatMessage(NetworkText.FromKey("Mods.SubworldLibraryCommunityFork.Move", Netplay.Clients[player].Name, subworlds[id].DisplayName), new Color(255, 240, 20), player);
				Player.Hooks.PlayerDisconnect(player);
			}

			// stop sending packets to the client while they're moving
			NetMessage.buffer[player].broadcast = false;

			if (id < ushort.MaxValue)
			{
				StartSubserver(id);
			}
		}

		internal static void FinishMove(int player)
		{
			RemoteClient client = Netplay.Clients[player];

			int id = pendingMoves[player];
			if (id == ushort.MaxValue)
			{
				if (Main.autoShutdown && client.Socket.GetRemoteAddress().IsLocalHost())
				{
					// this is reverted in the CheckBytes injection
					Main.autoShutdown = false;
					suppressAutoShutdown = player;
				}

				playerLocations[player] = -1;
				deniedSockets.Remove(client.Socket);

				// Main.player[player].active and Netplay.Clients[player].State must never disagree.
				// NetMessage.SyncConnectedPlayer gates on the former, NetMessage.SyncOnePlayer gates on
				// the latter, and when they disagree SyncOnePlayer falls into its disconnect branch,
				// which does SendData(14, -1, ...) - a broadcast of PlayerActive=false for this player
				// to every client, ignoring its own toWho argument. Clearing active here keeps the pair
				// consistent for the whole handshake, so the player is merely skipped instead.
				Main.player[player].active = false;

				ClearRejoinTracking(player);
				rejoining[player] = true;
				rejoinLastState[player] = 1;

				client.State = 1;
				client.ResetSections();

				// prompt the client to reconnect
				client.Socket.AsyncSend(new byte[] { 5, 0, 3, (byte)player, 0 }, 0, 5, (state) => { });

				pendingMoves[player] = -1;
				return;
			}

			NetMessage.buffer[player].broadcast = true;

			// prompt the client to reconnect, done before setting their location so the packet can go through
			SubserverLink link = subworlds[id].link;
			if (link != null && link.Connected)
			{
				client.Socket.AsyncSend(new byte[] { 5, 0, 3, (byte)player, 0 }, 0, 5, (state) => { });
			}

			// set the client's location. DenyRead and DenySend are now in effect
			playerLocations[player] = id;
			deniedSockets.Add(client.Socket);

			pendingMoves[player] = -1;
		}

		/// <summary>
		/// Repairs join handshakes that vanilla left half-done. Runs once per server tick, at the end of
		/// Netplay.UpdateServerInMainThread, on the main server and on subservers alike.
		/// <para/>
		/// MessageBuffer case 12 calls Player.Spawn - which sets Main.player[i].active - before it checks
		/// the connection state, so a handshake packet that arrives out of order leaves the server holding
		/// active == true with State &lt; 10. Vanilla only relays PlayerControls at State == 10, so the
		/// player stops moving for everyone, and every later SyncConnectedPlayer call routes them through
		/// SyncOnePlayer's disconnect branch, which does SendData(14, -1, ...) - a broadcast of
		/// PlayerActive=false for them to every client, ignoring its own toWho argument. They go invisible
		/// in world and on the map, permanently, with no vanilla path back.
		/// <para/>
		/// The check is on the invariant rather than on a "this player is rejoining" flag on purpose. The
		/// same break happens to players joining a subserver, where nothing in this library ever sets such
		/// a flag and where relaying through the main server makes reordering more likely, not less.
		/// active == true with State &lt; 10 is never a legitimate resting state - case 12 sets both in one
		/// block and RemoteClient.Reset clears both together - and packets are processed on the main thread,
		/// so observing it here, after every packet for the tick has been handled, is unambiguous.
		/// </summary>
		internal static void UpdateJoiningPlayers()
		{
			if (Main.netMode != NetmodeID.Server)
			{
				return;
			}

			for (int i = 0; i < 255; i++)
			{
				RemoteClient client = Netplay.Clients[i];

				if (!client.IsConnected())
				{
					ClearRejoinTracking(i);
					continue;
				}

				if (pendingMoves[i] >= 0 || playerLocations[i] >= 0)
				{
					ClearRejoinTracking(i);
					continue;
				}

				if (client.State == 10)
				{
					ClearRejoinTracking(i);
					continue;
				}

				if (Main.player[i].active)
				{
					ClearRejoinTracking(i);
					client.State = 10;
					NetMessage.buffer[i].broadcast = true;
					NetMessage.SyncConnectedPlayer(i);
					continue;
				}

				if (rejoining[i])
				{
					UpdateRejoinWatchdog(i, client);
				}
			}
		}

		// ~10 seconds at 60 ticks per second. Long enough that a slow WorldGen.clearWorld or map load on
		// the client is never mistaken for a stall.
		private const int RejoinStallTicks = 600;
		private const int MaxRejoinPrompts = 3;

		private static void UpdateRejoinWatchdog(int player, RemoteClient client)
		{
			if (client.State != rejoinLastState[player])
			{
				rejoinLastState[player] = client.State;
				rejoinStallTicks[player] = 0;
				return;
			}

			if (++rejoinStallTicks[player] < RejoinStallTicks)
			{
				return;
			}

			rejoinStallTicks[player] = 0;

			if (++rejoinPrompts[player] > MaxRejoinPrompts)
			{
				rejoining[player] = false;
				ModContent.GetInstance<SubworldLibrary>().Logger.Warn($"Player {player} stopped advancing at handshake state {client.State} while returning to the main world, and did not recover after {MaxRejoinPrompts} prompts.");
				return;
			}

			try
			{
				client.Socket.AsyncSend(new byte[] { 5, 0, 3, (byte)player, 0 }, 0, 5, (state) => { });
			}
			catch
			{
				// no-op
			}
		}

		private static void ClearRejoinTracking(int player)
		{
			rejoining[player] = false;
			rejoinLastState[player] = 0;
			rejoinStallTicks[player] = 0;
			rejoinPrompts[player] = 0;
		}

		private static void SyncDisconnect(int player)
		{
			ClearRejoinTracking(player);

			if (playerLocations[player] >= 0)
			{
				subworlds[playerLocations[player]].link?.Send(GetDisconnectPacket(player, ModContent.GetInstance<SubworldLibrary>().NetID));

				playerLocations[player] = -1;
			}

			deniedSockets.Remove(Netplay.Clients[player].Socket);
		}

		private static byte[] GetDisconnectPacket(int player, int id)
		{
			// client, (ushort) size, packet id, (byte/ushort) sublib net id twice (read a second time by sublib to sync a leaving client)
			if (ModNet.NetModCount < 256)
			{
				return new byte[] { (byte)player, 5, 0, 250, (byte)id, (byte)id };
			}
			else
			{
				return new byte[] { (byte)player, 7, 0, 250, (byte)id, (byte)(id >> 8), (byte)id, (byte)(id >> 8) };
			}
		}

		private static void AllowAutoShutdown(int i)
		{
			if (i == suppressAutoShutdown && Netplay.Clients[i].State == 10)
			{
				suppressAutoShutdown = -1;
				Main.autoShutdown = true;
			}
		}

		/// <summary>
		/// Starts a subserver for the subworld with the specified ID, if one is not running already.
		/// </summary>
		public static void StartSubserver(int id)
		{
			Subworld subworld = subworlds[id];
			if (subworld.link != null)
			{
				return;
			}

			string name = subworld.FileName;

			string args = "tModLoader.dll -server -showserverconsole ";

			args += Main.ActiveWorldFileData.IsCloudSave ? "-cloudworld \"" : "-world \"";

			args += Main.worldPathName + "\" -subworld \"" + name + "\"";

			if (Program.LaunchParameters.TryGetValue("-modpath", out string modpath))
			{
				args += " -modpath \"" + modpath + "\"";
			}
			if (Program.LaunchParameters.TryGetValue("-modpack", out string modpack))
			{
				args += " -modpack \"" + modpack + "\"";
			}
			if (Program.LaunchParameters.TryGetValue("-steamworkshopfolder", out string steamworkshopfolder))
			{
				args += " -steamworkshopfolder \"" + steamworkshopfolder + "\"";
			}
			if (Program.LaunchParameters.TryGetValue("-tmlsavedirectory", out string tmlsavedirectory))
			{
				args += " -tmlsavedirectory \"" + tmlsavedirectory + "\"";
			}
			if (Program.LaunchParameters.TryGetValue("-savedirectory", out string savedirectory))
			{
				args += " -savedirectory \"" + savedirectory + "\"";
			}
			if (Program.LaunchParameters.TryGetValue("-config", out string config))
			{
				args += " -config \"" + config + "\"";
			}
			if (Program.LaunchParameters.TryGetValue("-forcepriority", out string forcepriority))
			{
				args += " -forcepriority " + forcepriority;
			}
			if (Netplay.SpamCheck)
			{
				args += " -secure";
			}

			copiedData = [];
			CopyMainWorldData();

			var link = new SubserverLink(name, copiedData);
			subworld.link = link;
			copiedData = null;

			Process p = new Process();
			p.StartInfo.FileName = Environment.ProcessPath!;
			p.StartInfo.Arguments = args;
			p.StartInfo.UseShellExecute = true;
			p.EnableRaisingEvents = true;
			p.Exited += (_, _) => { if (!link.Closed) { StopSubserver(id); } }; // ensures the main server recognizes a subserver as stopped even if it crashes before the pipes can connect
			p.Start();

			new Thread(subworld.link.ConnectAndRead)
			{
				Name = "Subserver Packets",
				IsBackground = true
			}.Start(id);

			new Thread(subworld.link.ConnectAndSend)
			{
				Name = "Subserver Relay",
				IsBackground = true
			}.Start(id);
		}

		/// <summary>
		/// Stops a subserver for the subworld with the specified ID, if one is running.
		/// </summary>
		public static void StopSubserver(int id)
		{
			Subworld subworld = subworlds[id];
			if (subworld.link == null)
			{
				return;
			}

			subworld.link.Close();
			subworld.link = null;

			// Do not move the players back to the main server when the main server closes.
			// This prevents clients getting stuck on a loading screen; clients will disconnect naturally.
			if (Netplay.Disconnect)
			{
				return;
			}

			for (int i = 0; i < 256; i++)
			{
				if (playerLocations[i] == id)
				{
					playerLocations[i] = -1;
					deniedSockets.Remove(Netplay.Clients[i].Socket);

					pendingMoves[i] = ushort.MaxValue;

					ModPacket packet = ModContent.GetInstance<SubworldLibrary>().GetPacket();
					packet.Write(ushort.MaxValue);
					packet.Send(i);

					NetMessage.buffer[i].broadcast = false;
				}
			}
		}

		/// <summary>
		/// Tries to get the index of the subworld with the specified ID.
		/// <br/> Typically used for <see cref="Subworld.ReturnDestination"/>.
		/// <br/> Returns <see cref="int.MinValue"/> if the subworld couldn't be found.
		/// <code>public override int ReturnDestination => SubworldSystem.GetIndex("MyMod/MySubworld");</code>
		/// </summary>
		public static int GetIndex(string id)
		{
			for (int i = 0; i < subworlds.Count; i++)
			{
				if (subworlds[i].FullName == id)
				{
					return i;
				}
			}
			return int.MinValue;
		}

		/// <summary>
		/// Gets the index of the specified subworld.
		/// <br/> Typically used for <see cref="Subworld.ReturnDestination"/>.
		/// </summary>
		public static int GetIndex<T>() where T : Subworld
		{
			for (int i = 0; i < subworlds.Count; i++)
			{
				if (subworlds[i].GetType() == typeof(T))
				{
					return i;
				}
			}
			return int.MinValue;
		}

		private static byte[] GetPacketHeader(int size, int mod)
		{
			byte[] packet = new byte[size];

			packet[0] = 255; // invalid client under normal circumstances, message 255 from client 255 is treated as a packet from the other server

			packet[1] = (byte)(size - 1);
			packet[2] = (byte)((size - 1) >> 8);

			packet[3] = 255;

			packet[4] = (byte)mod;
			if (ModNet.NetModCount >= 256)
			{
				packet[5] = (byte)(mod >> 8);
			}

			return packet;
		}

		/// <summary>
		/// Sends a packet from the specified mod directly to a subserver.
		/// <br/> Use <see cref="GetIndex"/> to get the subserver's ID.
		/// </summary>
		public static void SendToSubserver(int subserver, Mod mod, byte[] data)
		{
			int header = ModNet.NetModCount < 256 ? 5 : 6;
			byte[] packet = GetPacketHeader(data.Length + header, mod.NetID);
			Buffer.BlockCopy(data, 0, packet, header, data.Length);
			subworlds[subserver].link?.Send(packet);
		}

		/// <summary>
		/// Sends a packet from the specified mod directly to all subservers.
		/// </summary>
		public static void SendToAllSubservers(Mod mod, byte[] data)
		{
			int header = ModNet.NetModCount < 256 ? 5 : 6;
			byte[] packet = GetPacketHeader(data.Length + header, mod.NetID);
			Buffer.BlockCopy(data, 0, packet, header, data.Length);

			for (int i = 0; i < subworlds.Count; i++)
			{
				subworlds[i].link?.Send(packet);
			}
		}

		/// <summary>
		/// Sends a packet from the specified mod directly to all subservers added by that mod.
		/// </summary>
		public static void SendToAllSubserversFromMod(Mod mod, byte[] data)
		{
			int header = ModNet.NetModCount < 256 ? 5 : 6;
			byte[] packet = GetPacketHeader(data.Length + header, mod.NetID);
			Buffer.BlockCopy(data, 0, packet, header, data.Length);

			for (int i = 0; i < subworlds.Count; i++)
			{
				Subworld subworld = subworlds[i];
				if (subworld.Mod == mod)
				{
					subworld.link?.Send(packet);
				}
			}
		}

		/// <summary>
		/// Sends a packet from the specified mod directly to the main server.
		/// </summary>
		public static void SendToMainServer(Mod mod, byte[] data)
		{
			int header = ModNet.NetModCount < 256 ? 5 : 6;
			byte[] packet = GetPacketHeader(data.Length + header, mod.NetID);
			Buffer.BlockCopy(data, 0, packet, header, data.Length);
			lock (queue)
			{
				Buffer.BlockCopy(packet, 0, queue, totalData, packet.Length);
				totalData += packet.Length;
			}
		}
	}
}
