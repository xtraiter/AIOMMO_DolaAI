using System.Text.Json.Serialization;

namespace DolaCoordinator.Models;

public class UpdateInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("releaseDate")]
    public string ReleaseDate { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("changelog")]
    public string Changelog { get; set; } = string.Empty;

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; } = false;

    [JsonPropertyName("sha256Checksum")]
    public string? Sha256Checksum { get; set; }
}
