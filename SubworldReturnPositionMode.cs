namespace SubworldLibraryCommunityFork
{
	/// <summary>
	/// Controls how an opted-in subworld restores player positions when the same instance is revisited.
	/// </summary>
	public enum SubworldReturnPositionMode
	{
		/// <summary>Do not remember or restore return positions.</summary>
		Disabled,
		/// <summary>Each player returns to the position they saved when leaving.</summary>
		PerPlayer,
		/// <summary>Players who previously left return to the most recently saved shared position.</summary>
		Shared,
	}
}
