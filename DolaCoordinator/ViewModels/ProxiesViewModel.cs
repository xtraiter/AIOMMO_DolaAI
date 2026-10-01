using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Proxy;
using DolaCoordinator.Services.Storage;
using DolaCoordinator.Views.Dialogs;

namespace DolaCoordinator.ViewModels;

/// <summary>
/// Trang "Quản lý proxy": danh sách proxy (thêm / nhập nhiều / sửa / xóa), kiểm tra IP thoát, và xem tài khoản nào đang dùng.
/// Việc gán proxy cho tài khoản nằm ở trang Quản lý tài khoản (nút "Gán proxy", cột Proxy).
/// </summary>
public partial class ProxiesViewModel : ObservableObject
{
    private readonly IProxyService _proxies;
    private readonly IDatabaseService _db;

    public ObservableCollection<ProxyItem> Items { get; } = new();

    [ObservableProperty] private string _statusText = "Thêm proxy rồi gán cho tài khoản ở trang Quản lý tài khoản (nút 'Gán proxy').";
    [ObservableProperty] private bool _isAllSelected;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _okCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private bool _isTestingAny;

    public ProxiesViewModel(IProxyService proxies, IDatabaseService db)
    {
        _proxies = proxies;
        _db = db;
        Load();
        WeakReferenceMessenger.Default.Register<ProxiesViewModel, ProxiesChangedMessage>(this, static (vm, _) => vm.Application_Reload());
    }

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var p in Items) p.IsSelected = value;
        UpdateCounters();
    }

    private void Application_Reload()
    {
        // tin nhắn có thể tới từ luồng khác
        if (Application.Current?.Dispatcher.CheckAccess() == false) Application.Current.Dispatcher.InvokeAsync(Load);
        else Load();
    }

    public void Load()
    {
        var selected = Items.Where(p => p.IsSelected).Select(p => p.Id).ToHashSet();
        Items.Clear();
        var profiles = _db.GetAllProfiles();
        foreach (var p in _proxies.GetAll())
        {
            var users = profiles.Where(a => a.ProxyId == p.Id).Select(a => a.Name).OrderBy(n => n).ToList();
            p.AccountCount = users.Count;
            p.AccountNames = users.Count <= 4 ? string.Join(", ", users) : string.Join(", ", users.Take(4)) + $"… (+{users.Count - 4})";
            p.IsSelected = selected.Contains(p.Id);
            p.PropertyChanged += OnItemChanged;
            Items.Add(p);
        }
        UpdateCounters();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProxyItem.IsSelected)) UpdateCounters();
    }

    private void UpdateCounters()
    {
        TotalCount = Items.Count;
        SelectedCount = Items.Count(p => p.IsSelected);
        OkCount = Items.Count(p => p.LastOk == true);
        FailedCount = Items.Count(p => p.LastOk == false);
    }

    private List<ProxyItem> Picked() => Items.Where(p => p.IsSelected).ToList();

    // ------------------------------------------------------------------ thêm / sửa / xóa

    [RelayCommand]
    private void AddProxy()
    {
        var dlg = new ProxyEditorWindow { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var r = dlg.Result;
        string Key(ProxyItem p) => $"{p.Scheme}://{p.Host}:{p.Port}|{p.Username}";
        if (_proxies.GetAll().Any(p => Key(p).Equals(Key(r), StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"Proxy {r.DisplayName} đã có trong danh sách — không thêm trùng.";
            return;
        }
        _proxies.Save(r, dlg.PlainPassword);
        StatusText = $"Đã thêm proxy {r.DisplayName}. Bấm 'Kiểm tra' để xem IP thoát.";
    }

    [RelayCommand]
    private void ImportProxiesFromFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excel / CSV (*.xlsx;*.csv;*.tsv)|*.xlsx;*.csv;*.tsv|Tất cả (*.*)|*.*",
            Title = "Nhập proxy từ file Excel/CSV",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var rows = TableFile.ReadRows(dialog.FileName);
            var (added, dup) = AddProxies(BackupIO.ProxiesFromRows(rows));
            StatusText = $"Đã nhập {added} proxy từ file" + (dup > 0 ? $", bỏ qua {dup} trùng." : ".");
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Không đọc được file: {ex.Message}", "Nhập proxy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Lưu danh sách proxy, bỏ qua trùng (scheme/host/port/user). Trả (đã thêm, bỏ qua).</summary>
    internal (int Added, int Duplicates) AddProxies(IEnumerable<(ProxyItem Item, string Password)> items)
    {
        string Key(ProxyItem p) => $"{p.Scheme}://{p.Host}:{p.Port}|{p.Username}";
        var existing = _proxies.GetAll().Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0, dup = 0;
        foreach (var (item, pass) in items)
        {
            if (!existing.Add(Key(item))) { dup++; continue; }
            _proxies.Save(item, pass);
            added++;
        }
        return (added, dup);
    }

    [RelayCommand]
    private void ExportProxies()
    {
        var list = _proxies.GetAll();
        if (list.Count == 0) { StatusText = "Chưa có proxy nào để xuất."; return; }

        if (MessageBox.Show(
                "File xuất chứa mật khẩu proxy ở dạng văn bản thường. Chỉ lưu ở nơi an toàn. Tiếp tục?",
                "Cảnh báo bảo mật", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = $"Dola_Proxy_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
            Title = "Xuất danh sách proxy",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            BackupIO.WriteProxies(dialog.FileName, list.Select(BuildProxyExportRow));
            StatusText = $"Đã xuất {list.Count} proxy ra {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Không xuất được file (đang mở trong Excel?): {ex.Message}", "Xuất proxy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Toàn bộ proxy ở dạng dòng xuất (cho "Sao lưu toàn bộ").</summary>
    internal IEnumerable<ProxyExportRow> ExportAllRows() => _proxies.GetAll().Select(BuildProxyExportRow).ToList();

    /// <summary>Khôi phục proxy từ các dòng (gồm tiêu đề). Trả số đã thêm.</summary>
    internal int RestoreFromRows(List<string[]> rows)
    {
        var (added, _) = AddProxies(BackupIO.ProxiesFromRows(rows));
        if (added > 0) WeakReferenceMessenger.Default.Send(new ProxiesChangedMessage());
        return added;
    }

    /// <summary>Gom một proxy (kèm mật khẩu đã giải mã qua BuildUrl) thành dòng xuất.</summary>
    internal ProxyExportRow BuildProxyExportRow(ProxyItem p)
    {
        var pass = ProxyParser.TryParse(_proxies.BuildUrl(p), out var pp, out _) && pp != null ? pp.Password ?? string.Empty : string.Empty;
        var country = p.LastOk == true ? p.LastCountry ?? string.Empty : string.Empty;
        return new ProxyExportRow(p.DisplayName, p.Scheme, p.Host, p.Port, p.Username ?? string.Empty, pass, country);
    }

    [RelayCommand]
    private void ImportProxies()
    {
        var dlg = new ProxyImportWindow { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var (parsed, errors) = ProxyParser.ParseLines(dlg.Text);
        var existing = _proxies.GetAll().Select(p => $"{p.Scheme}://{p.Host}:{p.Port}|{p.Username}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var duplicates = 0;
        foreach (var p in parsed)
        {
            if (!existing.Add($"{p.Scheme}://{p.Host}:{p.Port}|{p.Username}")) { duplicates++; continue; }
            _proxies.Save(new ProxyItem
            {
                Name = $"{p.Host}:{p.Port}",
                Scheme = p.Scheme,
                Host = p.Host,
                Port = p.Port,
                Username = p.Username,
            }, p.Password ?? string.Empty);
            added++;
        }
        StatusText = $"Đã nhập {added} proxy" + (duplicates > 0 ? $", bỏ qua {duplicates} proxy trùng" : string.Empty) +
                     (errors.Count > 0 ? $", {errors.Count} dòng lỗi: {string.Join(" | ", errors.Take(3))}" : string.Empty) + ".";
    }

    [RelayCommand]
    private void EditProxy(ProxyItem? proxy)
    {
        proxy ??= Picked().FirstOrDefault();
        if (proxy == null) { StatusText = "Tích chọn 1 proxy (hoặc bấm đúp vào một dòng) để sửa."; return; }
        var dlg = new ProxyEditorWindow(proxy) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        _proxies.Save(dlg.Result, dlg.PlainPassword);
        StatusText = proxy.AccountCount > 0
            ? $"Đã lưu proxy. {proxy.AccountCount} tài khoản đang dùng nó sẽ dùng thông tin mới từ lần mở Chromium kế tiếp."
            : "Đã lưu proxy.";
    }

    [RelayCommand]
    private void DeleteProxies()
    {
        var picked = Picked();
        if (picked.Count == 0) { StatusText = "Tích chọn proxy cần xóa."; return; }
        var using_ = picked.Sum(p => p.AccountCount);
        var question = $"Xóa {picked.Count} proxy?" + (using_ > 0
            ? Environment.NewLine + $"{using_} tài khoản đang dùng chúng sẽ quay về không dùng proxy." : string.Empty);
        if (MessageBox.Show(question, "Xóa proxy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var detached = picked.Sum(p => _proxies.Delete(p.Id));
        StatusText = $"Đã xóa {picked.Count} proxy" + (detached > 0 ? $", gỡ proxy khỏi {detached} tài khoản." : ".");
    }

    // ------------------------------------------------------------------ kiểm tra

    [RelayCommand]
    private async Task TestSelectedAsync()
    {
        var picked = Picked();
        if (picked.Count == 0) { StatusText = "Tích chọn proxy cần kiểm tra (hoặc bấm 'Kiểm tra tất cả')."; return; }
        await TestManyAsync(picked);
    }

    [RelayCommand]
    private async Task TestAllAsync() => await TestManyAsync(Items.ToList());

    private async Task TestManyAsync(List<ProxyItem> list)
    {
        if (list.Count == 0) return;
        IsTestingAny = true;
        StatusText = $"Đang kiểm tra {list.Count} proxy...";
        using var gate = new SemaphoreSlim(4); // tối đa 4 proxy cùng lúc
        try
        {
            await Task.WhenAll(list.Select(async p =>
            {
                await gate.WaitAsync();
                try
                {
                    p.IsTesting = true;
                    p.NotifyChanged();
                    await _proxies.TestAsync(p);
                }
                finally
                {
                    p.IsTesting = false;
                    p.NotifyChanged();
                    gate.Release();
                }
            }));
        }
        finally
        {
            IsTestingAny = false;
            UpdateCounters();
        }
        var ok = list.Count(p => p.LastOk == true);
        StatusText = $"Kiểm tra xong: {ok}/{list.Count} proxy chạy được.";
        WeakReferenceMessenger.Default.Send(new ProxiesChangedMessage()); // trang tài khoản đọc lại quốc gia
    }
}
