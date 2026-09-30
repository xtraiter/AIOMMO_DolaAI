using System.Collections.Generic;

namespace DolaCoordinator.Models;

/// <summary>Một nhân vật của prompt: tên, mô tả ngoại hình/tính cách và (các) ảnh tham chiếu của nhân vật đó.</summary>
public class PromptCharacter
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Ảnh tham chiếu của nhân vật (đường dẫn trên máy này), có thể nhiều ảnh (chính diện, nghiêng, toàn thân...).</summary>
    public List<string> Images { get; set; } = new();
}
