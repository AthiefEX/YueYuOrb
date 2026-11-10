using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantAssetLoaderTests
{
    [Fact]
    public async Task PngCharacterCardTextIsImportedWithoutChangingOriginalImage()
    {
        var path = Path.Combine(Path.GetTempPath(), "YueYu-Role-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a+1sAAAAASUVORK5CYII=");
            var json = "{\"spec\":\"chara_card_v2\",\"data\":{\"name\":\"PNG角色\",\"description\":\"仅供聊天的角色资料\"}}";
            var data = System.Text.Encoding.ASCII.GetBytes("chara\0" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)));
            using var stream = new MemoryStream(); stream.Write(image.AsSpan(0, image.Length - 12));
            var length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length); stream.Write(length); stream.Write("tEXt"u8); stream.Write(data);
            uint crc = uint.MaxValue;
            foreach (var b in "tEXt"u8.ToArray().Concat(data)) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); stream.Write(length); stream.Write(image.AsSpan(image.Length - 12));
            var original = stream.ToArray(); await File.WriteAllBytesAsync(path, original);
            var asset = await AssistantAssetLoader.LoadAsync(path, "role", CancellationToken.None);
            Assert.Equal("PNG角色", asset.Name); Assert.Contains("仅供聊天", asset.Text); Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SkillReferencesStayInsidePackageAndNeverExecuteScripts()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Assistant-Asset-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "SKILL.md"), "---\nname: Safe skill\n---\nRead [details](details.md). [script](run.ps1)");
            await File.WriteAllTextAsync(Path.Combine(root, "details.md"), "supporting text");
            await File.WriteAllTextAsync(Path.Combine(root, "run.ps1"), "throw 'must not run'");
            var asset = await AssistantAssetLoader.LoadAsync(root, "skill", CancellationToken.None);
            Assert.Equal("Safe skill", asset.Name); Assert.Contains("supporting text", asset.Text); Assert.DoesNotContain("must not run", asset.Text);
            await File.WriteAllTextAsync(Path.Combine(root, "SKILL.md"), "[escape](../outside.md)");
            await Assert.ThrowsAsync<InvalidDataException>(() => AssistantAssetLoader.LoadAsync(root, "skill", CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task VersionTwoCharacterCardBecomesRoleWithoutGrantingTools()
    {
        var path = Path.Combine(Path.GetTempPath(), "YueYu-Role-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"spec\":\"chara_card_v2\",\"data\":{\"name\":\"月语\",\"description\":\"helpful\",\"personality\":\"gentle\",\"system_prompt\":\"role instructions\"}}");
            var asset = await AssistantAssetLoader.LoadAsync(path, "role", CancellationToken.None);
            Assert.Equal("月语", asset.Name); Assert.Equal("role", asset.Kind); Assert.Contains("gentle", asset.Text); Assert.Equal(64, asset.Sha256.Length);
        }
        finally { File.Delete(path); }
    }
}
