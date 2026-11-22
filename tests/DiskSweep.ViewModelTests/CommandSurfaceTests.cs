using System.Reflection;
using DiskSweep.App.ViewModels;
using Xunit;

namespace DiskSweep.ViewModelTests;

/// <summary>命令面是 MVVM 的契约：XAML 绑定的每个 XxxCommand 都必须是真实生成的命令属性。</summary>
public class CommandSurfaceTests
{
    private static readonly Type[] ViewModelTypes =
    {
        typeof(MainViewModel), typeof(SettingsViewModel),
        typeof(PreviewViewModel), typeof(CategoryGroup)
    };

    private static string[] CommandsOf(Type t) => t
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.Name.EndsWith("Command", StringComparison.Ordinal))
        .Select(p => p.Name).ToArray();

    [Fact]
    public void MainViewModel_ExposesAllInteractionCommands()
    {
        var names = CommandsOf(typeof(MainViewModel));
        foreach (var expected in new[]
        {
            "StartScanCommand", "CancelScanCommand", "SelectLowRiskCommand", "CleanupCommand",
            "ElevatedCleanupCommand", "OpenSettingsCommand", "InitializeCommand",
            "ShowAnalysisViewCommand", "ShowCleanupViewCommand", "ShowSoftwareViewCommand",
            "RestartAsAdminCommand"
        })
            Assert.Contains(expected, names);

        Assert.DoesNotContain("ShowResultsViewCommand", names);
    }

    [Fact]
    public void EveryViewModel_InheritsObservableObject()
    {
        foreach (var type in ViewModelTypes)
            Assert.Equal("CommunityToolkit.Mvvm.ComponentModel.ObservableObject", type.BaseType?.FullName);
    }

    [Fact]
    public void EveryCommandProperty_IsARelayCommand()
    {
        var inspected = 0;
        foreach (var type in ViewModelTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.Name.EndsWith("Command", StringComparison.Ordinal)))
            {
                inspected++;
                var typeName = property.PropertyType.FullName ?? "";
                bool isCommand = typeName.Contains("RelayCommand", StringComparison.Ordinal) ||
                                 property.PropertyType.GetInterfaces()
                                     .Any(i => i.FullName == "System.Windows.Input.ICommand");
                Assert.True(isCommand, $"{type.Name}.{property.Name} 类型为 {typeName}，不是命令");
            }
        }
        Assert.True(inspected >= 20, $"至少应生成 20 个命令属性，实际 {inspected} 个");
    }

    [Fact]
    public void ViewModels_ExposeNoWpfVisibilityTypes()
    {
        foreach (var type in ViewModelTypes)
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Assert.DoesNotContain("Visibility", property.PropertyType.FullName ?? "");
    }
}
