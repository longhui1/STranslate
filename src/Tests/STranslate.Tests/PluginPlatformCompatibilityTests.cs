using STranslate.Core;
using System.Runtime.InteropServices;

namespace STranslate.Tests;

public class PluginPlatformCompatibilityTests
{
    [Theory]
    [InlineData("3410e7de989340938301abd6fcf8cc4b", Architecture.Arm64, false)]
    [InlineData("3410E7DE989340938301ABD6FCF8CC4B", Architecture.Arm64, false)]
    [InlineData("3410e7de989340938301abd6fcf8cc4b", Architecture.X64, true)]
    [InlineData("c5914774d4854623ad11912676c7007b", Architecture.Arm64, false)]
    [InlineData("26b37788a09c4999a296255eb0c95129", Architecture.Arm64, false)]
    [InlineData("26B37788A09C4999A296255EB0C95129", Architecture.Arm64, false)]
    [InlineData("c5914774d4854623ad11912676c7007b", Architecture.X64, true)]
    [InlineData("26b37788a09c4999a296255eb0c95129", Architecture.X64, true)]
    [InlineData("c67c0e3de45b48f6a852ffa8f0aae2f2", Architecture.Arm64, true)]
    // 社区 MiMo 的原始插件 ID 在 ARM64 上继续通过同一安装、加载流程。
    [InlineData("f508b0c220774c569db4c6133ff41cca", Architecture.Arm64, true)]
    public void KnownUnsupportedPlugin_IsExcludedOnlyFromNativeArm64(
        string pluginId, Architecture processArchitecture, bool expected)
    {
        Assert.Equal(expected, PluginManager.IsPluginSupported(pluginId, processArchitecture));
    }

    [Fact]
    public void Arm64PaddleRejectionPointsToTheNativeBuiltInPlugin()
    {
        var message = PluginManager.GetUnsupportedPluginMessage("26b37788a09c4999a296255eb0c95129", Architecture.Arm64);
        Assert.NotNull(message);
        Assert.Contains("PaddleOCR V6 (ARM64)", message);
        Assert.DoesNotContain("微信", message);
        Assert.Null(PluginManager.GetUnsupportedPluginMessage("26b37788a09c4999a296255eb0c95129", Architecture.X64));
    }
}
