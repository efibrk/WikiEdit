namespace WikiEdits;

/// <summary>Small replacements for APIs that .NET Framework 4.8 does not provide.</summary>
internal static class Compat
{
    /// <summary>Stands in for Math.Clamp, which was introduced in .NET Core 2.0.</summary>
    public static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>HTTP 429. HttpStatusCode.TooManyRequests does not exist on .NET Framework.</summary>
    public const System.Net.HttpStatusCode TooManyRequests = (System.Net.HttpStatusCode)429;
}
