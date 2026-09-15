namespace MediaOpsCore.Modules.Alerting.Domain;

public enum AlertType
{
    New,
    Ad,
    RepeatedWithinMinute,
    RepeatedOtherPlatform
}

// Legacy label mapping is a domain concern here, not a serialization detail: apps/web-api and the
// old w-service both key off these exact Spanish strings ("Nueva"/"Anuncio"/...), so the wire format
// IS the vocabulary, not an infrastructure choice.
public static class AlertTypeExtensions
{
    public static string ToLegacyLabel(this AlertType type) => type switch
    {
        AlertType.New => "Nueva",
        AlertType.Ad => "Anuncio",
        AlertType.RepeatedWithinMinute => "RepetidaMinuto",
        AlertType.RepeatedOtherPlatform => "RepetidaOtraPlataforma",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static AlertType FromLegacyLabel(string label) => label switch
    {
        "Nueva" => AlertType.New,
        "Anuncio" => AlertType.Ad,
        "RepetidaMinuto" => AlertType.RepeatedWithinMinute,
        "RepetidaOtraPlataforma" => AlertType.RepeatedOtherPlatform,
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Unknown legacy alert type label.")
    };
}
