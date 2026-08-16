using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Predicts the campfire-ignition buff (decompile <c>Campfire.Update</c>, the
    /// <c>!fireHasStarted</c> branch) for save capture. That buff - a flat +moraleBoostBaseline
    /// extra stamina via <c>MoraleBoost.SpawnMoraleBoost</c>, plus a flat -0.2 (20 points) each
    /// off Petrify and Injury via two <c>AdjustStatus</c> calls - lands on the next
    /// <c>Update()</c> tick after <c>Light_Rpc</c>, which is after
    /// <see cref="CampfireAutoSavePatch"/>'s postfix already wrote the save. All three are
    /// unconditional for anyone within <c>moraleBoostRadius</c> of the campfire at that moment -
    /// no difficulty/Ascent multiplier applies to any of them (Petrify bypasses
    /// <c>StatusAffectedByDifficultyMod</c> entirely; the multiplier only lives in
    /// <c>AddStatus</c>, which this Injury reduction never reaches since it's a
    /// <c>SubtractStatus</c> call; moraleBoostBaseline is a flat field, not scaled). The only
    /// gate is proximity.
    /// </summary>
    internal static class CampfireIgniteBuffCompat
    {
        /// <summary>
        /// Returns the post-buff (extraStamina, petrifyAmount) for this character if they're
        /// within range of <paramref name="campfire"/>, or the inputs unchanged otherwise.
        /// Order matches vanilla: the stamina grant is clamped against the pre-reduction
        /// petrify cap, then petrify drops - see <c>Character.SetExtraStamina</c> and
        /// <c>CharacterData.SetPetrify</c> for why that order doesn't matter here (petrify
        /// dropping only widens the cap, never re-tops-up the stamina that was already clamped).
        ///
        /// Also reduces <paramref name="currentStatuses"/>' Injury entry by 0.2 in place, if in
        /// range - same fixed heal the game applies alongside the Petrify reduction.
        /// </summary>
        public static (float extraStamina, int petrifyAmount) Apply(
            Campfire campfire, Character character, float rawExtraStamina, int rawPetrifyAmount, float[] currentStatuses)
        {
            if (campfire == null || character == null) return (rawExtraStamina, rawPetrifyAmount);

            float distance = Vector3.Distance(campfire.transform.position, character.Center);
            if (distance > campfire.moraleBoostRadius) return (rawExtraStamina, rawPetrifyAmount);

            float cap = 1f - (float)rawPetrifyAmount * 0.01f;
            float extraStamina = Mathf.Clamp(rawExtraStamina + campfire.moraleBoostBaseline, 0f, cap);
            int petrifyAmount = Mathf.Clamp(rawPetrifyAmount - 20, 0, 100);

            int injuryIndex = (int)CharacterAfflictions.STATUSTYPE.Injury;
            if (currentStatuses != null && injuryIndex < currentStatuses.Length)
                currentStatuses[injuryIndex] = Mathf.Clamp(currentStatuses[injuryIndex] - 0.2f, 0f, 1f);

            return (extraStamina, petrifyAmount);
        }
    }
}
