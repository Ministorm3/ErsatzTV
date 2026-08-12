namespace ErsatzTV.Core.Scheduling.YamlScheduling.Models;

public class YamlPlayoutCountInstruction : YamlPlayoutInstruction
{
    public string Count { get; set; }

    /// <summary>
    ///     Content key naming the media item the shared session plays instead of tuning the item's own
    ///     source, for the length of this item's window. The item keeps its own source and identity:
    ///     cohort viewers still get the live presentation through variant sessions, which only works
    ///     because the item still carries its remote stream source and templated url.
    ///     This is not the same thing as "fallback:" on duration / pad_to_next / pad_until, which
    ///     replaces the item outright.
    /// </summary>
    public string Slate { get; set; }
}
