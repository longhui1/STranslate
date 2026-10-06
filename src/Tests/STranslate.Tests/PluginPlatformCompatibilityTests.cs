using STranslate.Core;
using System.Runtime.InteropServices;

namespace STranslate.Tests;

public class PluginPlatformCompatibilityTests
{
    [Theory]
    [InlineData("3410e7de989340938301abd6fcf8cc4b", Architecture.Arm64, false)]
    [InlineData("3410E7DE989340938301ABD6FCF8CC4B", Architecture.Arm64, false)]
    [InlineData("3410e7de989340938301abd6fcf8cc4b", Architecture.X64, true)]
    // 社区 MiMo 的原始插件 ID 在 ARM64 上继续通过同一安装、加载流程。
    [InlineData("f508b0c220774c569db4c6133ff41cca", Architecture.Arm64, true)]
    public void KnownUnsupportedPlugin_IsExcludedOnlyFromNativeArm64(
        string pluginId, Architecture processArchitecture, bool expected)
    {
        Assert.Equal(expected, PluginManager.IsPluginSupported(pluginId, processArchitecture));
    }
}
