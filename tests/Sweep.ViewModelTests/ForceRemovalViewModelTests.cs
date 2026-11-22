using Sweep.App.Services;
using Sweep.App.ViewModels;
using Sweep.Core.Software;
using Xunit;

namespace Sweep.ViewModelTests;

/// <summary>
/// 强制删除的 ViewModel 流程：扫描只读 → 逐项勾选 → 二次确认 → 只删可删项。
/// 确认取消或无可勾选项时不执行任何删除。
/// </summary>
public class ForceRemovalViewModelTests
{
    private static SoftwareEntry Entry(string name, string location, string uninstall = @"C:\App\unins.exe") =>
        new("Machine64:" + name, name, "1.0", "Acme", location, "2025-01-02", uninstall,
            0, SoftwareScope.Machine64, false);

    private static (SoftwareViewModel Vm, FakeSoftwareService Service, FakeDialogs Dialogs,
        FakeForceRemovalService Force) Create(params ForceRemovalCandidate[] candidates)
    {
        var service = new FakeSoftwareService();
        var dialogs = new FakeDialogs();
        var force = new FakeForceRemovalService { Candidates = candidates };
        var vm = new SoftwareViewModel(service, dialogs, new FakeDispatcher(), 
            forceRemoval: force);
        return (vm, service, dialogs, force);
    }

    [Fact]
    public async Task ScanForceRemoval_IsReadOnly_AndPopulatesCandidates()
    {
        var good = new ForceRemovalCandidate(@"C:\Program Files\Acme", "Acme",
            ForceRemovalKind.Directory, true, "安装目录");
        var blocked = new ForceRemovalCandidate(@"C:\Windows", "Windows",
            ForceRemovalKind.Directory, false, "系统级");
        var (vm, service, _, force) = Create(good, blocked);
        service.Entries.Add(Entry("Acme", @"C:\Program Files\Acme"));
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];

        await vm.ScanForceRemovalCommand.ExecuteAsync(null);

        Assert.Equal(1, force.ScanCount);
        Assert.Equal(0, force.RemoveCount);   // 扫描绝不删除
        Assert.Equal(2, vm.ForceRemovalCandidates.Count);
        Assert.True(vm.HasForceRemovalCandidates);
        Assert.True(vm.ForceRemovalCandidates[0].CanDelete);
        Assert.False(vm.ForceRemovalCandidates[1].CanDelete);
    }

    [Fact]
    public async Task ForceRemove_RequiresConfirmation_AndOnlyDeletesCheckedDeletable()
    {
        var good = new ForceRemovalCandidate(@"C:\Program Files\Acme", "Acme",
            ForceRemovalKind.Directory, true, "安装目录");
        var blocked = new ForceRemovalCandidate(@"C:\Windows", "Windows",
            ForceRemovalKind.Directory, false, "系统级");
        var (vm, service, dialogs, force) = Create(good, blocked);
        service.Entries.Add(Entry("Acme", @"C:\Program Files\Acme"));
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.ScanForceRemovalCommand.ExecuteAsync(null);

        // 勾选系统级项（不可删）+ 可删项；用户取消确认 → 不删
        vm.ForceRemovalCandidates[0].IsSelected = true;
        vm.ForceRemovalCandidates[1].IsSelected = true;
        dialogs.ConfirmResult = false;
        await vm.ForceRemoveSelectedCommand.ExecuteAsync(null);
        Assert.Equal(0, force.RemoveCount);

        // 确认后：只把可删项送进服务
        dialogs.ConfirmResult = true;
        await vm.ForceRemoveSelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, force.RemoveCount);
        var removed = Assert.Single(force.RemovedCandidates!);
        Assert.Equal(@"C:\Program Files\Acme", removed.Path);
    }

    [Fact]
    public async Task ForceRemove_DisabledWhenNothingChecked()
    {
        var good = new ForceRemovalCandidate(@"C:\Program Files\Acme", "Acme",
            ForceRemovalKind.Directory, true, "安装目录");
        var (vm, service, dialogs, force) = Create(good);
        service.Entries.Add(Entry("Acme", @"C:\Program Files\Acme"));
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.ScanForceRemovalCommand.ExecuteAsync(null);

        await vm.ForceRemoveSelectedCommand.ExecuteAsync(null);   // 没勾选
        Assert.Equal(0, force.RemoveCount);
    }
}
