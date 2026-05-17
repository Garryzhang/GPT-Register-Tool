using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SmsWorkbench
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly string rootDir;
        private readonly ObservableCollection<PoolRow> allRows = new ObservableCollection<PoolRow>();
        private Process runningProcess;
        private int taskSeq = 1;
        private string searchText = "";
        private string countText = "1";
        private string pageSizeText = "25";
        private string proxyText = "";
        private object scopeFilter = "全部";
        private string luckmailTokenText = "";
        private string purchaseProjectText = "openai";
        private string purchaseEmailTypeText = "ms_imap";
        private string purchaseDomainText = "outlook.com";
        private bool skipPaypalLink;
        private string logText = "";
        private string statusText = "就绪";
        private string pageStatusText = "第 0/0 页";
        private string totalCountText = "0";
        private string mailboxCountText = "0";
        private string registeredCountText = "0";
        private string paypalCountText = "0";
        private string attentionCountText = "0";
        private int currentPage = 1;
        private int filteredCount;
        private bool sidebarCollapsed;

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<TaskRow> Tasks { get; } = new ObservableCollection<TaskRow>();

        public ObservableCollection<PoolRow> PagedRows { get; } = new ObservableCollection<PoolRow>();

        public PoolRow SelectedRow { get; set; }

        public int SelectedTabIndex { get; set; }

        public string SearchText
        {
            get => searchText;
            set { searchText = value ?? ""; OnPropertyChanged(nameof(SearchText)); currentPage = 1; RefreshPagedRows(); }
        }

        public string CountText
        {
            get => countText;
            set { countText = value ?? "1"; OnPropertyChanged(nameof(CountText)); }
        }

        public string PageSizeText
        {
            get => pageSizeText;
            set { pageSizeText = value ?? "25"; OnPropertyChanged(nameof(PageSizeText)); currentPage = 1; RefreshPagedRows(); }
        }

        public string ProxyText
        {
            get => proxyText;
            set { proxyText = value ?? ""; OnPropertyChanged(nameof(ProxyText)); }
        }

        public object ScopeFilter
        {
            get => scopeFilter;
            set { scopeFilter = value; OnPropertyChanged(nameof(ScopeFilter)); currentPage = 1; RefreshPagedRows(); }
        }

        public string LuckmailTokenText
        {
            get => luckmailTokenText;
            set { luckmailTokenText = value ?? ""; OnPropertyChanged(nameof(LuckmailTokenText)); }
        }

        public string PurchaseProjectText
        {
            get => purchaseProjectText;
            set { purchaseProjectText = value ?? ""; OnPropertyChanged(nameof(PurchaseProjectText)); }
        }

        public string PurchaseEmailTypeText
        {
            get => purchaseEmailTypeText;
            set { purchaseEmailTypeText = value ?? ""; OnPropertyChanged(nameof(PurchaseEmailTypeText)); }
        }

        public string PurchaseDomainText
        {
            get => purchaseDomainText;
            set { purchaseDomainText = value ?? ""; OnPropertyChanged(nameof(PurchaseDomainText)); }
        }

        public bool SkipPaypalLink
        {
            get => skipPaypalLink;
            set { skipPaypalLink = value; OnPropertyChanged(nameof(SkipPaypalLink)); }
        }

        public string LogText
        {
            get => logText;
            set { logText = value ?? ""; OnPropertyChanged(nameof(LogText)); }
        }

        public string StatusText
        {
            get => statusText;
            set { statusText = value ?? ""; OnPropertyChanged(nameof(StatusText)); }
        }

        public string PageStatusText
        {
            get => pageStatusText;
            set { pageStatusText = value ?? ""; OnPropertyChanged(nameof(PageStatusText)); }
        }

        public string TotalCountText
        {
            get => totalCountText;
            set { totalCountText = value ?? "0"; OnPropertyChanged(nameof(TotalCountText)); }
        }

        public string MailboxCountText
        {
            get => mailboxCountText;
            set { mailboxCountText = value ?? "0"; OnPropertyChanged(nameof(MailboxCountText)); }
        }

        public string RegisteredCountText
        {
            get => registeredCountText;
            set { registeredCountText = value ?? "0"; OnPropertyChanged(nameof(RegisteredCountText)); }
        }

        public string PaypalCountText
        {
            get => paypalCountText;
            set { paypalCountText = value ?? "0"; OnPropertyChanged(nameof(PaypalCountText)); }
        }

        public string AttentionCountText
        {
            get => attentionCountText;
            set { attentionCountText = value ?? "0"; OnPropertyChanged(nameof(AttentionCountText)); }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            rootDir = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;
            if (Path.GetFileName(rootDir).Equals("net10", StringComparison.OrdinalIgnoreCase))
            {
                rootDir = Directory.GetParent(Directory.GetParent(rootDir)?.FullName ?? rootDir)?.FullName ?? rootDir;
            }
            if (Path.GetFileName(rootDir).Equals("dist", StringComparison.OrdinalIgnoreCase))
            {
                rootDir = Directory.GetParent(rootDir)?.FullName ?? rootDir;
            }

            ScopeFilter = "全部";
            PurchaseProjectText = ConfigString("email_registration", "luckmail_purchase_project_code");
            if (PurchaseProjectText.Length == 0) PurchaseProjectText = "openai";
            PurchaseEmailTypeText = ConfigString("email_registration", "luckmail_purchase_email_type");
            if (PurchaseEmailTypeText.Length == 0) PurchaseEmailTypeText = "ms_imap";
            PurchaseDomainText = ConfigString("email_registration", "luckmail_purchase_domain");
            if (PurchaseDomainText.Length == 0) PurchaseDomainText = "outlook.com";
            RefreshPools();
        }

        private bool FilterRow(object item)
        {
            return item is PoolRow row && FilterRow(row);
        }

        private bool FilterRow(PoolRow row)
        {
            if (row == null) return false;
            string scope = DisplayText(ScopeFilter);
            string term = (SearchText ?? "").Trim().ToLowerInvariant();

            if (scope == "邮箱池" && !row.AccountType.Contains("邮箱池")) return false;
            if (scope == "已注册" && !row.AccountType.Contains("Session") && !row.AccountType.Contains("SQLite")) return false;
            if (scope == "待处理" && !row.Status.Contains("待") && !row.Status.Contains("缺") && !row.Status.Contains("失败")) return false;
            if (term.Length == 0) return true;

            string text = (row.Identifier + " " + row.AccountType + " " + row.Status + " " + row.Notes).ToLowerInvariant();
            return text.Contains(term);
        }

        private void RefreshPools()
        {
            allRows.Clear();
            LoadMailboxPool();
            LoadSessionPool();
            currentPage = 1;
            UpdateOverview();
            RefreshPagedRows();
            StatusText = $"共 {allRows.Count} 条；当前筛选 {filteredCount} 条";
            Log("邮箱池和 session 状态已刷新。");
        }

        private void RefreshPagedRows()
        {
            if (PagedRows == null) return;
            var filtered = allRows.Where(FilterRow).ToList();
            filteredCount = filtered.Count;
            int pageSize = PageSizeValue();
            int pageCount = Math.Max(1, (int)Math.Ceiling(filteredCount / (double)pageSize));
            if (currentPage < 1) currentPage = 1;
            if (currentPage > pageCount) currentPage = pageCount;

            PagedRows.Clear();
            foreach (PoolRow row in filtered.Skip((currentPage - 1) * pageSize).Take(pageSize))
            {
                PagedRows.Add(row);
            }

            int start = filteredCount == 0 ? 0 : (currentPage - 1) * pageSize + 1;
            int end = filteredCount == 0 ? 0 : Math.Min(filteredCount, currentPage * pageSize);
            PageStatusText = $"第 {currentPage}/{pageCount} 页，显示 {start}-{end} / {filteredCount}";
            StatusText = $"共 {allRows.Count} 条；当前筛选 {filteredCount} 条";
        }

        private void UpdateOverview()
        {
            int mailboxes = allRows.Count(r => r.AccountType.Contains("邮箱池"));
            int registered = allRows.Count(IsRegisteredRow);
            int paypal = allRows.Count(r => r.Status.Contains("PayPal"));
            int attention = allRows.Count(r => r.Status.Contains("待") || r.Status.Contains("缺") || r.Status.Contains("失败"));
            TotalCountText = allRows.Count.ToString();
            MailboxCountText = mailboxes.ToString();
            RegisteredCountText = registered.ToString();
            PaypalCountText = paypal.ToString();
            AttentionCountText = attention.ToString();
        }

        private bool IsRegisteredRow(PoolRow row)
        {
            return row.AccountType.Contains("Session")
                || row.AccountType.Contains("SQLite")
                || row.Status.Contains("已注册")
                || row.Status.Contains("PayPal");
        }

        private void LoadMailboxPool()
        {
            string tokenFile = GetMailboxTokenFile();
            LoadMailboxTokenFile(tokenFile);
        }

        private void LoadMailboxTokenFile(string path)
        {
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] parts = line.Split(new[] { "---" }, StringSplitOptions.None);
                if (parts.Length < 3) continue;
                allRows.Add(new PoolRow
                {
                    Id = "M" + (i + 1),
                    CreatedAt = SafeTime(File.GetLastWriteTime(path)),
                    CompletedAt = SafeTime(File.GetLastWriteTime(path)),
                    Identifier = parts[0].Trim(),
                    AccountType = "邮箱池",
                    Status = "已授权",
                    RefreshToken = Mask(parts[2]),
                    Notes = path,
                    SourcePath = path,
                    RawLine = line
                });
            }
        }

        private void LoadSessionPool()
        {
            if (LoadSessionDatabase())
            {
                return;
            }
            LoadSessionJsonPool();
        }

        private bool LoadSessionDatabase()
        {
            string dbPath = GetDatabasePath();
            if (!File.Exists(dbPath)) return false;
            try
            {
                EnsureAccountExtraColumns(dbPath);
                string sql = "SELECT id,email,access_token,status,error,paypal_ok,paypal_url,paypal_status,refresh_token_status,json_path,pipeline_total_seconds,timing_total_seconds,created_at,updated_at FROM accounts ORDER BY updated_at DESC";
                var rows = SqliteNative.Query(dbPath, sql);
                if (rows.Count == 0) return false;
                foreach (Dictionary<string, string> data in rows)
                {
                    string status = data.TryGetValue("status", out string rawStatus) ? rawStatus : "";
                    string error = data.TryGetValue("error", out string rawError) ? rawError : "";
                    string paypalOk = data.TryGetValue("paypal_ok", out string rawPaypalOk) ? rawPaypalOk : "";
                    string paypalUrl = data.TryGetValue("paypal_url", out string rawPaypalUrl) ? rawPaypalUrl : "";
                    string paypalStatus = data.TryGetValue("paypal_status", out string rawPaypalStatus) ? rawPaypalStatus : "";
                    string refreshTokenStatus = data.TryGetValue("refresh_token_status", out string rawRefreshTokenStatus) ? rawRefreshTokenStatus : "";
                    string access = data.TryGetValue("access_token", out string rawAccess) ? rawAccess : "";
                    string jsonPath = data.TryGetValue("json_path", out string rawJsonPath) ? rawJsonPath : "";
                    allRows.Add(new PoolRow
                    {
                        Id = "DB" + data["id"],
                        CreatedAt = UnixTimeText(data.TryGetValue("created_at", out string created) ? created : ""),
                        CompletedAt = UnixTimeText(data.TryGetValue("updated_at", out string updated) ? updated : ""),
                        Identifier = data.TryGetValue("email", out string email) ? email : "",
                        AccountType = "SQLite",
                        Status = DisplayAccountStatus(status, paypalOk, access, error, paypalStatus, refreshTokenStatus),
                        PayPalStatus = DisplayPayPalStatus(paypalStatus, paypalOk, paypalUrl),
                        RefreshTokenStatus = DisplayRefreshTokenStatus(refreshTokenStatus),
                        PayPalUrl = paypalUrl,
                        RefreshToken = Mask(access),
                        Proxy = DbTimingText(data),
                        Notes = string.IsNullOrWhiteSpace(jsonPath) ? dbPath : jsonPath,
                        SourcePath = dbPath,
                        RawLine = data["id"]
                    });
                }
                Log("已从 SQLite 加载账号索引：" + dbPath);
                return true;
            }
            catch (Exception ex)
            {
                Log("读取 SQLite 失败，回退读取 JSON：" + ex.Message);
                return false;
            }
        }

        private void LoadSessionJsonPool()
        {
            var dirs = new List<string>();
            string sessionsDir = GetSessionsDir();
            if (Directory.Exists(sessionsDir)) dirs.Add(sessionsDir);
            dirs.Add(rootDir);

            foreach (string dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string path in Directory.GetFiles(dir, "session_*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        Dictionary<string, object> data = ReadJsonObject(path);
                        string email = GetString(data, "email");
                        string access = GetString(data, "access_token");
                        string paypalStatus = GetPaypalStatus(data);
                        string paypalUrl = GetPaypalUrl(data);
                        string refreshTokenStatus = GetString(data, "refresh_token_status");
                        string timing = GetTimingText(data);
                        allRows.Add(new PoolRow
                        {
                            Id = "S" + (allRows.Count + 1),
                            CreatedAt = SafeTime(File.GetCreationTime(path)),
                            CompletedAt = SafeTime(File.GetLastWriteTime(path)),
                            Identifier = email,
                            AccountType = "Session",
                            Status = access.Length > 0 ? paypalStatus : "缺access_token",
                            PayPalStatus = paypalStatus,
                            RefreshTokenStatus = DisplayRefreshTokenStatus(refreshTokenStatus),
                            PayPalUrl = paypalUrl,
                            RefreshToken = Mask(access),
                            Proxy = timing,
                            Notes = path,
                            SourcePath = path
                        });
                    }
                    catch (Exception ex)
                    {
                        Log("读取 session 失败：" + path + " " + ex.Message);
                    }
                }
            }
        }

        private void EnsureAccountExtraColumns(string dbPath)
        {
            string[] migrations =
            {
                "ALTER TABLE accounts ADD COLUMN paypal_status TEXT DEFAULT ''",
                "ALTER TABLE accounts ADD COLUMN paypal_updated_at INTEGER DEFAULT 0",
                "ALTER TABLE accounts ADD COLUMN refresh_token_status TEXT DEFAULT ''",
                "ALTER TABLE accounts ADD COLUMN refresh_token_updated_at INTEGER DEFAULT 0",
                "ALTER TABLE accounts ADD COLUMN oauth_refresh_token TEXT DEFAULT ''"
            };
            foreach (string sql in migrations)
            {
                try { SqliteNative.Execute(dbPath, sql); }
                catch { }
            }
            try
            {
                SqliteNative.Execute(dbPath, "UPDATE accounts SET paypal_status='link_ready' WHERE (paypal_status IS NULL OR paypal_status='') AND paypal_url IS NOT NULL AND paypal_url<>''");
                SqliteNative.Execute(dbPath, "UPDATE accounts SET refresh_token_status='missing' WHERE refresh_token_status IS NULL OR refresh_token_status=''");
            }
            catch { }
        }

        private void BuyAndRegister_Click(object sender, RoutedEventArgs e)
        {
            var args = new List<string> { "--buy-luckmail-mailbox", "--count", CountValue().ToString() };
            AddPurchaseArgs(args);
            AddProxy(args);
            AddPaypalOption(args);
            RunBackend("购买邮箱并注册", args);
        }

        private void RegisterFromPool_Click(object sender, RoutedEventArgs e)
        {
            var args = new List<string> { "--count", CountValue().ToString() };
            AddProxy(args);
            AddPaypalOption(args);
            RunBackend("邮箱池注册", args);
        }

        private void RegisterWithToken_Click(object sender, RoutedEventArgs e)
        {
            string token = (LuckmailTokenText ?? "").Trim();
            if (token.Length == 0)
            {
                MessageBox.Show("请先输入 LuckMail 邮箱 token。", "缺少 token", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var args = new List<string> { "--luckmail-token", token };
            AddProxy(args);
            AddPaypalOption(args);
            RunBackend("Token注册", args);
        }

        private void RebuildSqlite_Click(object sender, RoutedEventArgs e)
        {
            var args = new List<string> { "--rebuild-sqlite" };
            RunBackend("重建SQLite索引", args);
        }

        private void AccountGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (AccountGrid.SelectedItem is PoolRow row)
            {
                ShowAccountDetail(row);
            }
        }

        private void AccountDetail_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PoolRow row)
            {
                ShowAccountDetail(row);
            }
        }

        private void RunBackend(string taskName, List<string> args)
        {
            if (runningProcess != null && !runningProcess.HasExited)
            {
                MessageBox.Show("已有批次正在运行，请先取消或等待完成。", "运行中", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string script = Path.Combine(rootDir, "chatgpt_phone_reg.py");
            if (!File.Exists(script))
            {
                MessageBox.Show("找不到后端脚本：" + script, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var task = new TaskRow { Name = "批次 " + taskSeq++, Task = taskName, Status = "运行中", Info = string.Join(" ", args) };
            Tasks.Add(task);
            DateTime started = DateTime.Now;

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = Quote(script) + " " + JoinArgs(args),
                WorkingDirectory = rootDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            runningProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            runningProcess.OutputDataReceived += (_, ev) => { if (ev.Data != null) UiLog(ev.Data); };
            runningProcess.ErrorDataReceived += (_, ev) => { if (ev.Data != null) UiLog(ev.Data); };
            runningProcess.Exited += (_, __) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    task.Status = runningProcess.ExitCode == 0 ? "完成" : "失败";
                    task.Cost = ((int)(DateTime.Now - started).TotalSeconds).ToString();
                    task.DoneAt = SafeTime(DateTime.Now);
                    StatusText = taskName + " 已结束";
                    RefreshPools();
                }), DispatcherPriority.Background);
            };

            try
            {
                Log("启动：" + psi.FileName + " " + psi.Arguments);
                runningProcess.Start();
                runningProcess.BeginOutputReadLine();
                runningProcess.BeginErrorReadLine();
                StatusText = taskName + " 运行中";
            }
            catch (Exception ex)
            {
                task.Status = "启动失败";
                Log("启动失败：" + ex.Message);
            }
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = allRows.Where(r => r.IsChecked).ToList();
            if (selected.Count == 0 && SelectedRow != null) selected.Add(SelectedRow);
            if (selected.Count == 0)
            {
                MessageBox.Show("请先勾选或选择要删除的记录。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show("确定删除选中的 " + selected.Count + " 条记录？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            foreach (PoolRow row in selected) DeleteRow(row);
            RefreshPools();
        }

        private void DeleteRow(PoolRow row)
        {
            try
            {
                if (row.SourcePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(row.SourcePath);
                    Log("删除文件：" + row.SourcePath);
                    return;
                }
                if (row.SourcePath.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase))
                {
                    SqliteNative.Execute(row.SourcePath, "DELETE FROM accounts WHERE id=" + OnlyDigits(row.RawLine));
                    if (File.Exists(row.Notes) && IsUnderDirectory(row.Notes, GetSessionsDir()))
                    {
                        File.Delete(row.Notes);
                    }
                    Log("删除SQLite记录：" + row.Identifier);
                    return;
                }
                if (File.Exists(row.SourcePath) && !string.IsNullOrWhiteSpace(row.RawLine))
                {
                    var lines = File.ReadAllLines(row.SourcePath, Encoding.UTF8).ToList();
                    lines.RemoveAll(line => line.Trim() == row.RawLine.Trim());
                    File.WriteAllLines(row.SourcePath, lines, Encoding.UTF8);
                    Log("删除池记录：" + row.Identifier);
                }
            }
            catch (Exception ex)
            {
                Log("删除失败：" + row.Identifier + " " + ex.Message);
            }
        }

        private void CancelBatch_Click(object sender, RoutedEventArgs e)
        {
            if (runningProcess == null || runningProcess.HasExited)
            {
                Log("当前没有运行中的批次。");
                return;
            }
            try
            {
                runningProcess.Kill(true);
                Log("已取消当前批次。");
            }
            catch (Exception ex)
            {
                Log("取消失败：" + ex.Message);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPools();

        private void Settings_Click(object sender, RoutedEventArgs e) => ShowConfigDialog();

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            sidebarCollapsed = !sidebarCollapsed;
            SidebarColumn.Width = new GridLength(sidebarCollapsed ? 64 : 220);
            SidebarHeaderText.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            SidebarNavScroll.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            SidebarBottomActions.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            SidebarToggleButton.Content = sidebarCollapsed ? "›" : "‹";
        }

        private void OpenSessions_Click(object sender, RoutedEventArgs e) => OpenPath(GetSessionsDir());

        private void OpenDatabase_Click(object sender, RoutedEventArgs e) => OpenPath(GetDatabasePath());

        private void OpenMailboxPool_Click(object sender, RoutedEventArgs e) => OpenPath(GetMailboxTokenFile());

        private void OpenPayPalLink_Click(object sender, RoutedEventArgs e)
        {
            PoolRow row = SelectedAccountRow();
            if (row == null) return;
            if (string.IsNullOrWhiteSpace(row.PayPalUrl))
            {
                MessageBox.Show("选中账号没有可打开的 PayPal 支付链接。", "无支付链接", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenPayPalUrl(row.PayPalUrl);
        }

        private void RegeneratePayPalLink_Click(object sender, RoutedEventArgs e)
        {
            PoolRow row = SelectedAccountRow();
            if (row == null) return;
            var args = new List<string> { "--email", row.Identifier, "--regenerate-paypal-link" };
            AddSessionFileArg(args, row);
            RunBackend("重新生成PayPal链接", args);
        }

        private void MarkPayPalComplete_Click(object sender, RoutedEventArgs e)
        {
            PoolRow row = SelectedAccountRow();
            if (row == null) return;
            var args = new List<string> { "--email", row.Identifier, "--mark-paypal-status", "completed" };
            RunBackend("标记支付完成", args);
        }

        private void RefreshSession_Click(object sender, RoutedEventArgs e)
        {
            PoolRow row = SelectedAccountRow();
            if (row == null) return;
            var args = new List<string> { "--email", row.Identifier, "--refresh-session" };
            AddSessionFileArg(args, row);
            RunBackend("刷新Session", args);
        }

        private void AddSessionFileArg(List<string> args, PoolRow row)
        {
            string jsonPath = File.Exists(row.Notes) && row.Notes.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? row.Notes
                : row.SourcePath;
            if (File.Exists(jsonPath) && jsonPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--session-file");
                args.Add(jsonPath);
            }
        }

        private PoolRow SelectedAccountRow()
        {
            PoolRow row = SelectedRow ?? (AccountGrid.SelectedItem as PoolRow);
            if (row == null)
            {
                MessageBox.Show("请先选择一条账号记录。", "未选择账号", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return row;
        }

        private void ApplyFilter_Click(object sender, RoutedEventArgs e)
        {
            currentPage = 1;
            RefreshPagedRows();
        }

        private void ShowAll_Click(object sender, RoutedEventArgs e) => SetScope("全部");

        private void ShowMailboxPool_Click(object sender, RoutedEventArgs e) => SetScope("邮箱池");

        private void ShowRegistered_Click(object sender, RoutedEventArgs e) => SetScope("已注册");

        private void ShowPending_Click(object sender, RoutedEventArgs e) => SetScope("待处理");

        private void FirstPage_Click(object sender, RoutedEventArgs e)
        {
            currentPage = 1;
            RefreshPagedRows();
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            currentPage--;
            RefreshPagedRows();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            currentPage++;
            RefreshPagedRows();
        }

        private void LastPage_Click(object sender, RoutedEventArgs e)
        {
            int pageSize = PageSizeValue();
            int count = allRows.Count(FilterRow);
            currentPage = Math.Max(1, (int)Math.Ceiling(count / (double)pageSize));
            RefreshPagedRows();
        }

        private void SetScope(string scope)
        {
            ScopeFilter = scope;
            currentPage = 1;
            RefreshPagedRows();
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            foreach (PoolRow row in allRows) row.IsChecked = false;
        }

        private void ShowAccountDetail(PoolRow row)
        {
            if (row == null) return;
            string detail = BuildAccountDetail(row);
            string paypalUrl = row.PayPalUrl ?? "";
            var dialog = new Window
            {
                Title = "账号详情 - " + row.Identifier,
                Owner = this,
                Width = 940,
                Height = 720,
                MinWidth = 760,
                MinHeight = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)FindResource("AppBg")
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = row.Identifier,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMain"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var summary = new Grid
            {
                Margin = new Thickness(0, 0, 0, 10),
                Background = (System.Windows.Media.Brush)FindResource("PanelBg")
            };
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 5; i++) summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddDetailRow(summary, 0, "邮箱", row.Identifier);
            AddDetailRow(summary, 1, "支付状态", row.PayPalStatus);
            AddDetailRow(summary, 2, "Refresh", row.RefreshTokenStatus);
            AddDetailRow(summary, 3, "更新时间", row.CompletedAt);
            AddDetailRow(summary, 4, "支付订阅链接", paypalUrl);
            Grid.SetRow(summary, 1);
            root.Children.Add(summary);

            var text = new TextBox
            {
                Text = detail,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMain"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 260,
                Background = (System.Windows.Media.Brush)FindResource("PanelBg")
            };
            Grid.SetRow(text, 2);
            root.Children.Add(text);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var openPayPalButton = new Button { Content = "打开支付链接", Width = 108, IsEnabled = !string.IsNullOrWhiteSpace(paypalUrl) };
            openPayPalButton.Click += (_, __) => OpenPayPalUrl(paypalUrl);
            var copyPayPalButton = new Button { Content = "复制支付链接", Width = 108, IsEnabled = !string.IsNullOrWhiteSpace(paypalUrl) };
            copyPayPalButton.Click += (_, __) => CopyPayPalUrl(paypalUrl);
            var openButton = new Button { Content = "打开源文件", Width = 96 };
            openButton.Click += (_, __) => OpenPath(File.Exists(row.Notes) ? row.Notes : row.SourcePath);
            var closeButton = new Button { Content = "关闭", Width = 72 };
            closeButton.Click += (_, __) => dialog.Close();
            actions.Children.Add(openPayPalButton);
            actions.Children.Add(copyPayPalButton);
            actions.Children.Add(openButton);
            actions.Children.Add(closeButton);
            Grid.SetRow(actions, 3);
            root.Children.Add(actions);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        private void AddDetailRow(Grid parent, int row, string label, string value)
        {
            var labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(10, 7, 10, 7),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSub")
            };
            Grid.SetRow(labelBlock, row);
            Grid.SetColumn(labelBlock, 0);
            parent.Children.Add(labelBlock);

            var valueBox = new TextBox
            {
                Text = value ?? "",
                Margin = new Thickness(0, 4, 10, 4),
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = (System.Windows.Media.Brush)FindResource("PanelBg"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextMain"),
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(valueBox, row);
            Grid.SetColumn(valueBox, 1);
            parent.Children.Add(valueBox);
        }

        private string BuildAccountDetail(PoolRow row)
        {
            var lines = new List<string>
            {
                "email: " + row.Identifier,
                "type: " + row.AccountType,
                "status: " + row.Status,
                "created_at: " + row.CreatedAt,
                "updated_at: " + row.CompletedAt,
                "source: " + row.Notes,
                ""
            };

            try
            {
                if (row.SourcePath.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase))
                {
                    string sql = "SELECT * FROM accounts WHERE id=" + OnlyDigits(row.RawLine);
                    var rows = SqliteNative.Query(row.SourcePath, sql);
                    if (rows.Count > 0)
                    {
                        foreach (KeyValuePair<string, string> item in rows[0])
                        {
                            lines.Add(item.Key + ": " + MaskSensitiveField(item.Key, item.Value));
                        }
                    }
                    return string.Join(Environment.NewLine, lines);
                }

                if (File.Exists(row.SourcePath) && row.SourcePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> data = ReadJsonObject(row.SourcePath);
                    AppendJsonDetail(lines, data, "");
                }
            }
            catch (Exception ex)
            {
                lines.Add("detail_error: " + ex.Message);
            }
            return string.Join(Environment.NewLine, lines);
        }

        private void AppendJsonDetail(List<string> lines, Dictionary<string, object> data, string prefix)
        {
            foreach (KeyValuePair<string, object> item in data)
            {
                string key = string.IsNullOrEmpty(prefix) ? item.Key : prefix + "." + item.Key;
                if (item.Value is Dictionary<string, object> nested)
                {
                    AppendJsonDetail(lines, nested, key);
                    continue;
                }
                if (item.Value is List<object> list)
                {
                    lines.Add(key + ": [" + list.Count + " item(s)]");
                    continue;
                }
                lines.Add(key + ": " + MaskSensitiveField(key, Convert.ToString(item.Value) ?? ""));
            }
        }

        private string MaskSensitiveField(string key, string value)
        {
            string lower = (key ?? "").ToLowerInvariant();
            if (lower.Contains("token") || lower.Contains("cookie") || lower.Contains("password") || lower.Contains("api_key"))
            {
                return Mask(value);
            }
            return value ?? "";
        }

        private void ShowConfigDialog()
        {
            string path = Path.Combine(rootDir, "config.json");
            EnsureConfigFile(path);
            var config = ReadJsonObject(path);
            var email = GetSection(config, "email_registration");
            var paypal = GetSection(config, "paypal");
            var storage = GetSection(config, "storage");
            var output = GetSection(config, "output");

            var dialog = new Window
            {
                Title = "配置",
                Owner = this,
                Width = 720,
                Height = 620,
                MinWidth = 640,
                MinHeight = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)FindResource("AppBg")
            };

            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var form = new Grid();
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var fields = new Dictionary<string, TextBox>();
            int row = 0;
            AddConfigField(form, fields, row++, "LuckMail API Key", "luckmail_api_key", GetString(email, "luckmail_api_key"));
            AddConfigField(form, fields, row++, "LuckMail Base URL", "luckmail_base_url", GetString(email, "luckmail_base_url"));
            AddConfigField(form, fields, row++, "购买项目", "luckmail_purchase_project_code", GetString(email, "luckmail_purchase_project_code"));
            AddConfigField(form, fields, row++, "邮箱类型", "luckmail_purchase_email_type", GetString(email, "luckmail_purchase_email_type"));
            AddConfigField(form, fields, row++, "邮箱域名", "luckmail_purchase_domain", GetString(email, "luckmail_purchase_domain"));
            AddConfigField(form, fields, row++, "OTP轮询间隔秒", "otp_poll_interval", GetString(email, "otp_poll_interval"));
            AddConfigField(form, fields, row++, "邮箱池文件", "token_file", GetString(email, "token_file"));
            AddConfigField(form, fields, row++, "PayPal代理", "paypal_proxy", FirstListValue(paypal, "proxies"));
            AddConfigField(form, fields, row++, "Session目录", "output_directory", GetString(output, "directory"));
            AddConfigField(form, fields, row++, "SQLite路径", "sqlite_path", GetString(storage, "sqlite_path"));

            var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 0);
            root.Children.Add(scroll);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var openJsonButton = new Button { Content = "打开JSON", Width = 88 };
            openJsonButton.Click += (_, __) => OpenPath(path);
            var saveButton = new Button { Content = "保存", Width = 72, Style = (Style)FindResource("PrimaryButton") };
            saveButton.Click += (_, __) =>
            {
                email["luckmail_api_key"] = fields["luckmail_api_key"].Text.Trim();
                email["luckmail_base_url"] = fields["luckmail_base_url"].Text.Trim();
                email["luckmail_purchase_project_code"] = fields["luckmail_purchase_project_code"].Text.Trim();
                email["luckmail_purchase_email_type"] = fields["luckmail_purchase_email_type"].Text.Trim();
                email["luckmail_purchase_domain"] = fields["luckmail_purchase_domain"].Text.Trim();
                email["otp_poll_interval"] = fields["otp_poll_interval"].Text.Trim();
                email["token_file"] = fields["token_file"].Text.Trim();
                paypal["proxies"] = new List<object> { fields["paypal_proxy"].Text.Trim() };
                output["directory"] = fields["output_directory"].Text.Trim();
                storage["sqlite_path"] = fields["sqlite_path"].Text.Trim();
                config["email_registration"] = email;
                config["paypal"] = paypal;
                config["output"] = output;
                config["storage"] = storage;
                SaveConfig(path, config);
                PurchaseProjectText = fields["luckmail_purchase_project_code"].Text.Trim();
                PurchaseEmailTypeText = fields["luckmail_purchase_email_type"].Text.Trim();
                PurchaseDomainText = fields["luckmail_purchase_domain"].Text.Trim();
                Log("配置已保存。");
                dialog.Close();
            };
            var cancelButton = new Button { Content = "取消", Width = 72 };
            cancelButton.Click += (_, __) => dialog.Close();
            actions.Children.Add(openJsonButton);
            actions.Children.Add(saveButton);
            actions.Children.Add(cancelButton);
            Grid.SetRow(actions, 1);
            root.Children.Add(actions);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        private void AddConfigField(Grid form, Dictionary<string, TextBox> fields, int row, string label, string key, string value)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSub"),
                Margin = new Thickness(0, 0, 12, 10)
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            form.Children.Add(text);

            var box = new TextBox
            {
                Text = value ?? "",
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            form.Children.Add(box);
            fields[key] = box;
        }

        private Dictionary<string, object> GetSection(Dictionary<string, object> config, string section)
        {
            if (config.TryGetValue(section, out object value) && value is Dictionary<string, object> map)
            {
                return map;
            }
            var created = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            config[section] = created;
            return created;
        }

        private string FirstListValue(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out object value) && value is List<object> list && list.Count > 0)
            {
                return Convert.ToString(list[0]) ?? "";
            }
            return "";
        }

        private void SaveConfig(string path, Dictionary<string, object> config)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(config, options), Encoding.UTF8);
        }

        private void EnsureConfigFile(string path)
        {
            if (File.Exists(path)) return;
            string example = Path.Combine(rootDir, "config.example.json");
            if (File.Exists(example))
            {
                File.Copy(example, path);
            }
            else
            {
                File.WriteAllText(path, "{}", Encoding.UTF8);
            }
        }

        private void AddProxy(List<string> args)
        {
            if (!string.IsNullOrWhiteSpace(ProxyText))
            {
                args.Add("--proxy");
                args.Add(ProxyText.Trim());
            }
        }

        private void AddPurchaseArgs(List<string> args)
        {
            if (!string.IsNullOrWhiteSpace(PurchaseProjectText))
            {
                args.Add("--luckmail-purchase-project");
                args.Add(PurchaseProjectText.Trim());
            }
            if (!string.IsNullOrWhiteSpace(PurchaseEmailTypeText))
            {
                args.Add("--luckmail-purchase-email-type");
                args.Add(PurchaseEmailTypeText.Trim());
            }
            if (!string.IsNullOrWhiteSpace(PurchaseDomainText))
            {
                args.Add("--luckmail-purchase-domain");
                args.Add(PurchaseDomainText.Trim());
            }
        }

        private void AddPaypalOption(List<string> args)
        {
            if (SkipPaypalLink)
            {
                args.Add("--skip-paypal-link");
            }
        }

        private int CountValue()
        {
            return int.TryParse(CountText, out int value) && value > 0 ? value : 1;
        }

        private int PageSizeValue()
        {
            return int.TryParse(PageSizeText, out int value) && value > 0 ? Math.Min(value, 500) : 25;
        }

        private string GetSessionsDir()
        {
            return Path.Combine(rootDir, "sessions");
        }

        private string GetDatabasePath()
        {
            string configured = ConfigString("storage", "sqlite_path");
            if (configured.Length == 0) return Path.Combine(rootDir, "runtime", "accounts.sqlite3");
            string expanded = Environment.ExpandEnvironmentVariables(configured);
            return Path.IsPathRooted(expanded) ? expanded : Path.Combine(rootDir, expanded);
        }

        private string GetMailboxTokenFile()
        {
            string configured = ConfigString("email_registration", "token_file");
            return configured.Length > 0 ? Environment.ExpandEnvironmentVariables(configured) : Path.Combine(rootDir, "mailbox_tokens.txt");
        }

        private string ConfigString(string section, string key)
        {
            string path = Path.Combine(rootDir, "config.json");
            if (!File.Exists(path)) return "";
            try
            {
                Dictionary<string, object> data = ReadJsonObject(path);
                if (!data.TryGetValue(section, out object sectionObj)) return "";
                if (sectionObj is not Dictionary<string, object> sectionData) return "";
                return sectionData.TryGetValue(key, out object value) ? Convert.ToString(value) ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        private string GetPaypalStatus(Dictionary<string, object> data)
        {
            if (!TryGetMap(data, "paypal", out Dictionary<string, object> paypal) || paypal.Count == 0)
            {
                return "已保存";
            }
            string status = GetString(data, "paypal_status");
            if (status.Length == 0) status = GetString(paypal, "status");
            if (status.Equals("completed", StringComparison.OrdinalIgnoreCase)) return "支付完成";
            if (status.Equals("link_ready", StringComparison.OrdinalIgnoreCase)) return "待人工支付";
            string ok = GetString(paypal, "ok").ToLowerInvariant();
            if (ok == "true") return "PayPal已生成";
            string error = GetString(paypal, "error");
            return error.Length > 0 ? "PayPal失败" : "已保存";
        }

        private string GetPaypalUrl(Dictionary<string, object> data)
        {
            if (!TryGetMap(data, "paypal", out Dictionary<string, object> paypal)) return "";
            return GetString(paypal, "url");
        }

        private string GetTimingText(Dictionary<string, object> data)
        {
            if (TryGetMap(data, "pipeline_timing", out Dictionary<string, object> pipeline))
            {
                string total = GetString(pipeline, "total_seconds");
                if (total.Length > 0) return total + "s";
            }
            if (TryGetMap(data, "timing", out Dictionary<string, object> timing))
            {
                string total = GetString(timing, "total_seconds");
                if (total.Length > 0) return total + "s";
            }
            if (TryGetMap(data, "paypal", out Dictionary<string, object> paypal))
            {
                return GetString(paypal, "proxy");
            }
            return "";
        }

        private string DisplayAccountStatus(string status, string paypalOk, string access, string error, string paypalStatus, string refreshTokenStatus)
        {
            if (!string.IsNullOrWhiteSpace(error) || status.Equals("failed", StringComparison.OrdinalIgnoreCase)) return "失败";
            if (paypalStatus.Equals("completed", StringComparison.OrdinalIgnoreCase) && refreshTokenStatus.Equals("oauth_present", StringComparison.OrdinalIgnoreCase)) return "已刷新";
            if (paypalStatus.Equals("completed", StringComparison.OrdinalIgnoreCase)) return "待刷新";
            if (paypalOk == "1" || status.Equals("paypal_ready", StringComparison.OrdinalIgnoreCase)) return "PayPal已生成";
            return access.Length > 0 ? "已注册" : "待处理";
        }

        private string DisplayPayPalStatus(string paypalStatus, string paypalOk, string paypalUrl)
        {
            if (paypalStatus.Equals("completed", StringComparison.OrdinalIgnoreCase)) return "支付完成";
            if (paypalStatus.Equals("failed", StringComparison.OrdinalIgnoreCase)) return "支付失败";
            if (paypalStatus.Equals("link_ready", StringComparison.OrdinalIgnoreCase)) return "待人工支付";
            if (paypalOk == "1" && !string.IsNullOrWhiteSpace(paypalUrl)) return "待人工支付";
            if (!string.IsNullOrWhiteSpace(paypalUrl)) return "待人工支付";
            return "";
        }

        private string DisplayRefreshTokenStatus(string refreshTokenStatus)
        {
            if (refreshTokenStatus.Equals("oauth_present", StringComparison.OrdinalIgnoreCase)) return "已获取";
            if (refreshTokenStatus.Equals("legacy_present", StringComparison.OrdinalIgnoreCase)) return "旧token";
            if (refreshTokenStatus.Equals("missing", StringComparison.OrdinalIgnoreCase)) return "缺失";
            return refreshTokenStatus ?? "";
        }

        private string DbTimingText(Dictionary<string, string> data)
        {
            string pipeline = data.TryGetValue("pipeline_total_seconds", out string pipelineSeconds) ? pipelineSeconds : "";
            if (!string.IsNullOrWhiteSpace(pipeline) && pipeline != "0.0" && pipeline != "0") return pipeline + "s";
            string timing = data.TryGetValue("timing_total_seconds", out string timingSeconds) ? timingSeconds : "";
            return string.IsNullOrWhiteSpace(timing) || timing == "0.0" || timing == "0" ? "" : timing + "s";
        }

        private string UnixTimeText(string raw)
        {
            if (!long.TryParse(raw, out long seconds) || seconds <= 0) return "";
            return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private string OnlyDigits(string raw)
        {
            string digits = new string((raw ?? "").Where(char.IsDigit).ToArray());
            return digits.Length == 0 ? "0" : digits;
        }

        private bool IsUnderDirectory(string path, string directory)
        {
            try
            {
                string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullDir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullPath.Equals(fullDir, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(fullDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetMap(Dictionary<string, object> data, string key, out Dictionary<string, object> map)
        {
            map = null;
            if (!data.TryGetValue(key, out object value)) return false;
            map = value as Dictionary<string, object>;
            return map != null;
        }

        private Dictionary<string, object> ReadJsonObject(string path)
        {
            var output = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return output;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                output[property.Name] = JsonValueToObject(property.Value);
            }
            return output;
        }

        private object JsonValueToObject(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String: return element.GetString() ?? "";
                case JsonValueKind.Number:
                    return element.TryGetInt64(out long n) ? n : element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Object:
                    var obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty property in element.EnumerateObject()) obj[property.Name] = JsonValueToObject(property.Value);
                    return obj;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(JsonValueToObject).ToList();
                default: return "";
            }
        }

        private string GetString(Dictionary<string, object> data, string key)
        {
            return data.TryGetValue(key, out object value) && value != null ? Convert.ToString(value) ?? "" : "";
        }

        private string DisplayText(object value)
        {
            if (value is ComboBoxItem item) return Convert.ToString(item.Content) ?? "";
            return Convert.ToString(value) ?? "";
        }

        private string JoinArgs(List<string> args) => string.Join(" ", args.Select(Quote));

        private string Quote(string value)
        {
            value ??= "";
            return value.IndexOfAny(new[] { ' ', '\t', '"', '&', '|' }) < 0 ? value : "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private string Mask(string value)
        {
            value = (value ?? "").Trim();
            return value.Length <= 12 ? value : value.Substring(0, 6) + "..." + value.Substring(value.Length - 4);
        }

        private string SafeTime(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss");

        private void OpenPath(string path)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return;
                }
                if (Path.GetExtension(path).Length > 0)
                {
                    string example = Path.Combine(rootDir, "config.example.json");
                    if (Path.GetFileName(path).Equals("config.json", StringComparison.OrdinalIgnoreCase) && File.Exists(example))
                    {
                        File.Copy(example, path);
                    }
                    Process.Start(new ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
                    return;
                }
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log("打开失败：" + ex.Message);
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    Log("无效链接：" + url);
                    return;
                }
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log("打开链接失败：" + ex.Message);
            }
        }

        private void OpenPayPalUrl(string url)
        {
            if (!IsHttpUrl(url))
            {
                Log("无效支付链接：" + url);
                return;
            }
            string chrome = FindChromePath();
            if (chrome.Length == 0)
            {
                Log("未找到 Chrome，使用系统默认浏览器打开支付链接。");
                OpenUrl(url);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = chrome,
                    Arguments = "--incognito " + Quote(url),
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                Log("Chrome 无痕打开失败：" + ex.Message);
                OpenUrl(url);
            }
        }

        private void CopyPayPalUrl(string url)
        {
            if (!IsHttpUrl(url))
            {
                Log("无效支付链接，无法复制。");
                return;
            }
            try
            {
                Clipboard.SetText(url);
                Log("支付链接已复制。");
            }
            catch (Exception ex)
            {
                Log("复制支付链接失败：" + ex.Message);
            }
        }

        private string FindChromePath()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
            };
            return candidates.FirstOrDefault(File.Exists) ?? "";
        }

        private bool IsHttpUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private void Log(string text)
        {
            LogText += "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine;
        }

        private void UiLog(string text)
        {
            Dispatcher.BeginInvoke(new Action(() => Log(text)), DispatcherPriority.Background);
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class PoolRow : INotifyPropertyChanged
    {
        private bool isChecked;
        public string Id { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string CompletedAt { get; set; } = "";
        public string Identifier { get; set; } = "";
        public string AccountType { get; set; } = "";
        public string Status { get; set; } = "";
        public string PayPalStatus { get; set; } = "";
        public string RefreshTokenStatus { get; set; } = "";
        public string PayPalUrl { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string Proxy { get; set; } = "";
        public string Notes { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string RawLine { get; set; } = "";
        public bool IsChecked
        {
            get => isChecked;
            set { isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class TaskRow : INotifyPropertyChanged
    {
        private string status = "";
        private string cost = "";
        private string doneAt = "";
        public string Name { get; set; } = "";
        public string Task { get; set; } = "";
        public string Info { get; set; } = "";
        public string Retry { get; set; } = "0";
        public string Status { get => status; set { status = value; Notify(nameof(Status)); } }
        public string Cost { get => cost; set { cost = value; Notify(nameof(Cost)); } }
        public string DoneAt { get => doneAt; set { doneAt = value; Notify(nameof(DoneAt)); } }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal static class SqliteNative
    {
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private const int SQLITE_OPEN_READONLY = 0x00000001;
        private const int SQLITE_OPEN_READWRITE = 0x00000002;

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int numBytes, out IntPtr stmt, IntPtr tail);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr stmt);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_name(IntPtr stmt, int index);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int index);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int index);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        public static List<Dictionary<string, string>> Query(string path, string sql)
        {
            IntPtr db = Open(path, SQLITE_OPEN_READONLY);
            try
            {
                IntPtr stmt = Prepare(db, sql);
                try
                {
                    var rows = new List<Dictionary<string, string>>();
                    int columnCount = sqlite3_column_count(stmt);
                    while (sqlite3_step(stmt) == SQLITE_ROW)
                    {
                        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < columnCount; i++)
                        {
                            row[PtrToString(sqlite3_column_name(stmt, i), -1)] = ColumnText(stmt, i);
                        }
                        rows.Add(row);
                    }
                    return rows;
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
            }
            finally
            {
                sqlite3_close(db);
            }
        }

        public static void Execute(string path, string sql)
        {
            IntPtr db = Open(path, SQLITE_OPEN_READWRITE);
            try
            {
                IntPtr stmt = Prepare(db, sql);
                try
                {
                    int code = sqlite3_step(stmt);
                    if (code != SQLITE_DONE && code != SQLITE_ROW) throw new InvalidOperationException(Error(db));
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
            }
            finally
            {
                sqlite3_close(db);
            }
        }

        private static IntPtr Open(string path, int flags)
        {
            int code = sqlite3_open_v2(NullTerminatedUtf8(path), out IntPtr db, flags, IntPtr.Zero);
            if (code != SQLITE_OK) throw new InvalidOperationException(Error(db));
            return db;
        }

        private static IntPtr Prepare(IntPtr db, string sql)
        {
            int code = sqlite3_prepare_v2(db, NullTerminatedUtf8(sql), -1, out IntPtr stmt, IntPtr.Zero);
            if (code != SQLITE_OK) throw new InvalidOperationException(Error(db));
            return stmt;
        }

        private static string Error(IntPtr db) => PtrToString(sqlite3_errmsg(db), -1);

        private static string ColumnText(IntPtr stmt, int index)
        {
            int bytes = sqlite3_column_bytes(stmt, index);
            return PtrToString(sqlite3_column_text(stmt, index), bytes);
        }

        private static string PtrToString(IntPtr ptr, int bytes)
        {
            if (ptr == IntPtr.Zero) return "";
            if (bytes < 0)
            {
                int len = 0;
                while (Marshal.ReadByte(ptr, len) != 0) len++;
                bytes = len;
            }
            byte[] buffer = new byte[bytes];
            Marshal.Copy(ptr, buffer, 0, bytes);
            return Encoding.UTF8.GetString(buffer);
        }

        private static byte[] NullTerminatedUtf8(string value)
        {
            byte[] body = Encoding.UTF8.GetBytes(value ?? "");
            byte[] output = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, output, 0, body.Length);
            return output;
        }
    }
}
