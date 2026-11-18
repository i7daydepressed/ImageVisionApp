namespace ImageVisionApp.Models;

public enum MediaKind {
    Image,
    Video
}

public sealed record MediaDocument(MediaKind Kind, string FileName, string? SourcePath);
