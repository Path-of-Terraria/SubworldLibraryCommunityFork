using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace SubworldLibraryCommunityFork
{
	internal sealed class ReturnPositionPlayer : ModPlayer
	{
		public override void SaveData(TagCompound tag)
		{
			if (Main.netMode != NetmodeID.Server)
			{
				SubworldSystem.SavePlayerReturnPositions(tag);
			}
		}

		public override void LoadData(TagCompound tag)
		{
			if (Main.netMode != NetmodeID.Server)
			{
				SubworldSystem.LoadPlayerReturnPositions(tag);
			}
		}
	}
}
