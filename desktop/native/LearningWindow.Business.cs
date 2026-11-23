using System.Globalization;
using System.Text.Json;

namespace Xdc.NativeDesktop;

internal sealed partial class LearningWindow
{
    private static readonly CultureInfo BusinessCulture = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly TimeSpan CampusOffset = TimeSpan.FromHours(8);
    private Func<Task>? businessRefresh;
    private int businessRefreshPage = -1;
    private int businessReadRevision;
    private int overviewPage = -1;
    private int downloadsPage = -1;
    private int downloadRevision;
    private bool accountRequestPending;
    private JsonElement currentJobs;

    private sealed class CourseLocation(string courseId, string tab)
    {
        public string Id { get; } = courseId;
        public string Tab { get; set; } = tab;
    }

    private Task ShowOverview() => LoadOverview(false);

    private Task LoadOverview(bool refresh)
    {
        DescribeRoute("overview");
        overviewPage = PageVersion;
        SetBusinessRefresh(() => LoadOverview(true));
        return ReadBusinessPage("学习总览", async () =>
        {
            var overview = CallAsync("overview.load", new { refresh });
            var account = ReadAccountForOverview();
            await Task.WhenAll(overview, account);
            return (await overview, await account);
        }, DrawOverview);
    }

    private async Task RefreshOverviewFromTimer()
    {
        if (IsDisposed) return;
        if (overviewPage == PageVersion) await LoadOverview(true);
        else await CallAsync("overview.load", new { refresh = true });
    }

    private async Task<JsonElement> ReadAccountForOverview()
    {
        try { return await CallAsync("auth.status"); }
        catch { return default; }
    }

    private void DrawOverview((JsonElement Data, JsonElement Account) model)
    {
        var (data, account) = model;
        BeginPage("学习总览", DateTimeOffset.UtcNow.ToOffset(CampusOffset).ToString("M月d日 dddd", BusinessCulture));
        bool signedOut = Field(account.Get("courses"), "state") == "signed-out";
        if (signedOut)
        {
            Row("学在浙大尚未登录", null, Go(ShowAccount), "登录");
            if (!Items(data, "deadlines").Any() && !Items(data, "newMaterials").Any()) return;
        }

        DrawServiceErrors(Items(data, "errors"));
        Section("未来 7 天", "作业截止");
        DrawItems(Items(data, "deadlines"), "暂无即将截止的作业", item =>
            Row(Field(item, "title", "作业"), Field(item, "course"),
                Go(() => ShowAssignment(Field(item, "course_id"), Field(item, "id"))), LocalDate(Field(item, "due_at"))));
        Section("新课件", "学在浙大");
        DrawItems(Items(data, "newMaterials"), "暂无新课件", file =>
            Row(Field(file, "name", "附件"), Field(file, "course_name"),
                BusinessAction(() => OpenCourse(Field(file, "course_id"), "materials")), "›"));
    }

    private Task ShowCourses() => LoadCourses(false);

    private Task LoadCourses(bool refresh)
    {
        DescribeRoute("courses");
        SetBusinessRefresh(() => LoadCourses(true));
        return ReadBusinessPage("我的课程", () => CallAsync("courses.list", new { refresh }), data =>
        {
            BeginPage("我的课程");
            var edit = (GlassButton)ActionButton("编辑", Go(() => ShowCourseEditor(false)), true);
            edit.UseGlassBackground = true;
            AddContent(edit);
            DrawItems(Items(data, "courses"), "暂无显示的课程", course =>
                Row(Field(course, "name", "课程"), CourseLabels(course),
                    BusinessAction(() => OpenCourse(Field(course, "id"), "summary")), "›"));
        });
    }

    private Task ShowCourseEditor(bool refresh)
    {
        DescribeRoute("courses");
        SetBusinessRefresh(() => ShowCourseEditor(true));
        return ReadBusinessPage("编辑课程", () => CallAsync("courses.list", new { refresh, includeHidden = true }), data =>
        {
            BeginPage("编辑课程", "勾选要显示的课程");
            var hidden = Items(data, "hiddenIds").Select(Scalar).ToHashSet(StringComparer.Ordinal);
            var checkedCourses = new List<(string Id, CheckBox Control)>();
            var buttons = BusinessBar();
            var cancel = SizedBusinessButton("取消", BusinessAction(GoBack));
            bool saving = false;
            var save = SizedBusinessButton("完成", BusinessAction(async () =>
            {
                if (saving) return;
                saving = true;
                int page = PageVersion;
                cancel.Enabled = false;
                foreach (var (_, checkbox) in checkedCourses) checkbox.Enabled = false;
                try
                {
                    var next = new HashSet<string>(hidden, StringComparer.Ordinal);
                    foreach (var (id, checkbox) in checkedCourses)
                    {
                        if (checkbox.Checked) next.Remove(id);
                        else next.Add(id);
                    }
                    if (!next.SetEquals(hidden)) await CallAsync("courses.visibility", new { hiddenIds = next.ToArray() });
                    if (page == PageVersion) await GoBack();
                }
                finally
                {
                    saving = false;
                    if (!cancel.IsDisposed) cancel.Enabled = true;
                    foreach (var (_, checkbox) in checkedCourses)
                        if (!checkbox.IsDisposed) checkbox.Enabled = true;
                }
            }), true);
            buttons.Controls.AddRange([cancel, save]);
            AddContent(buttons);
            DrawItems(Items(data, "courses"), "暂无课程", course =>
            {
                string id = Field(course, "id");
                var checkbox = BusinessCheck("显示 " + Field(course, "name", "课程"), !hidden.Contains(id));
                checkedCourses.Add((id, checkbox));
                AddContent(checkbox);
                if (CourseLabels(course) is { Length: > 0 } detail) Row(detail);
            });
        });
    }

    private Task OpenCourse(string courseId, string tab)
    {
        var location = new CourseLocation(courseId, tab);
        return ChangePage(() => LoadCourse(location, false));
    }

    private Task LoadCourse(CourseLocation location, bool refresh)
    {
        DescribeRoute("course", courseId: location.Id, tab: location.Tab);
        SetBusinessRefresh(() => LoadCourse(location, true));
        return ReadBusinessPage("课程详情", () => CallAsync("course.detail", new { courseId = location.Id, refresh }),
            data => DrawCourse(location, location.Tab, data));
    }

    private void DrawCourse(CourseLocation location, string tab, JsonElement data)
    {
        location.Tab = tab;
        string courseId = location.Id;
        DescribeRoute("course", courseId: courseId, tab: tab);
        var course = data.Get("course");
        BeginPage(Field(course, "name", "课程详情"), CourseLabels(course));
        BusinessButtons(
            ("概览", () => DrawCourse(location, "summary", data), tab == "summary"),
            ("作业", () => DrawCourse(location, "homework", data), tab == "homework"),
            ("资料", () => DrawCourse(location, "materials", data), tab == "materials"),
            ("智云", () => DrawCourse(location, "lecture", data), tab == "lecture"));
        DrawServiceErrors(Items(data, "errors"));

        var assignments = Items(data, "assignments").ToArray();
        var materials = Items(data, "materials").ToArray();
        var classroom = data.Get("classroom");
        switch (tab)
        {
            case "homework":
                DrawItems(assignments, "暂无作业", assignment =>
                    Row(Field(assignment, "title", "作业"), IsTrue(assignment, "submitted") ? "已提交" :
                        IsTrue(assignment, "overdue") ? "已逾期" : null,
                        Go(() => ShowAssignment(courseId, Field(assignment, "id"))), LocalDate(Field(assignment, "due_at"))));
                break;
            case "materials":
                DrawAttachments(materials, courseId, null, "暂无课程资料");
                break;
            case "lecture":
                DrawClassroom(location, classroom);
                break;
            default:
                Row(Field(assignments.FirstOrDefault(), "title", "作业"), $"{assignments.Length} 项作业",
                    () => DrawCourse(location, "homework", data), "›");
                Row(Field(materials.FirstOrDefault(), "name", "课程资料"), $"{materials.Length} 份资料",
                    () => DrawCourse(location, "materials", data), "›");
                var classroomTitle = classroom.Get("link").ValueKind == JsonValueKind.Object
                    ? Field(Items(classroom, "sessions").FirstOrDefault(), "session_name", "课堂课件") : "关联智云课程";
                Row(classroomTitle, "智云课堂", () => DrawCourse(location, "lecture", data), "›");
                break;
        }
    }

    private void DrawClassroom(CourseLocation location, JsonElement classroom)
    {
        var link = classroom.Get("link");
        if (link.ValueKind == JsonValueKind.Object)
        {
            Section("课堂课件", Field(link, "mode") == "manual" ? "已手动关联" : "已关联");
            DrawItems(Items(classroom, "sessions"), "本月暂无课堂", session =>
                Row(Field(session, "session_name", Field(session, "course_name", "课堂")), Field(session, "date"),
                    Go(() => ShowClassroomSession(session)), "›"));
            AddContent(ActionButton("解除关联", BusinessAction(() => ChangeClassroomLink(location, null, Field(classroom, "month")))));
            return;
        }
        Section("关联智云课程");
        DrawItems(Items(classroom, "choices"), "未找到可关联的智云课程", choice =>
            Row(Field(choice, "course_name", "课程"), Field(choice, "lecturer"),
                BusinessAction(() => ChangeClassroomLink(location, Field(choice, "course_id"), Field(classroom, "month"))), "关联"));
    }

    private async Task ChangeClassroomLink(CourseLocation location, string? classroomCourseId, string month)
    {
        int page = PageVersion;
        var payload = new Dictionary<string, object?> { ["courseId"] = location.Id, ["classroomCourseId"] = classroomCourseId };
        if (month.Length > 0) payload["month"] = month;
        await CallAsync("classroom.link", payload);
        if (page != PageVersion) return;
        Notice(classroomCourseId is null ? "已解除关联" : "已关联智云课程");
        await LoadCourse(location, true);
    }

    private Task ShowClassroomSession(JsonElement session)
    {
        DescribeRoute("session");
        BeginPage(Field(session, "session_name", "课堂详情"),
            JoinDetails(Field(session, "course_name"), Field(session, "date")));
        Section("课堂课件", "智云课堂");
        BusinessButtons(("导出 PDF", BusinessAction(() => QueueClassroomSession(session, false)), true),
            ("另存为", BusinessAction(() => QueueClassroomSession(session, true)), false));
        return Task.CompletedTask;
    }

    private async Task QueueClassroomSession(JsonElement session, bool alternate)
    {
        string? directory = alternate ? ChooseFolder() : null;
        if (alternate && directory is null) return;
        string date = Field(session, "date");
        var payload = new Dictionary<string, object>
        {
            ["source"] = "classroom",
            ["courseId"] = Field(session, "course_id"),
            ["sessionId"] = Field(session, "session_id"),
        };
        if (date.Length >= 7) payload["month"] = date[..7];
        if (directory is not null) payload["directory"] = directory;
        await CallAsync("download.enqueue", payload);
        Notice("已加入下载队列");
    }

    private Task ShowAssignment(string courseId, string assignmentId)
    {
        DescribeRoute("assignment", courseId: courseId, assignmentId: assignmentId);
        SetBusinessRefresh(() => ShowAssignment(courseId, assignmentId));
        return ReadBusinessPage("作业详情", () => CallAsync("assignment.detail", new { courseId, assignmentId }), data =>
        {
            var item = data.Get("assignment");
            BeginPage(Field(item, "title", "作业详情"),
                JoinDetails(Field(item, "course"), LocalDate(Field(item, "due_at"))));
            Section("作业要求");
            string description = Field(item, "description");
            if (description.Length == 0) Row("暂无文字要求");
            else
            {
                TextBox text = Input("作业要求", description, multiline: true);
                text.ReadOnly = true;
                text.ScrollBars = ScrollBars.Vertical;
                text.Height = Dip(130);
                AddContent(text);
            }
            Section("附件");
            DrawAttachments(Items(data, "attachments"), courseId, assignmentId, "暂无附件");
        });
    }

    private void DrawAttachments(IEnumerable<JsonElement> files, string courseId, string? assignmentId, string empty)
    {
        DrawItems(files, empty, file =>
        {
            string id = Field(file, "id");
            bool directlyDownloadable = IsTrue(file, "downloadable");
            bool preview = !directlyDownloadable && IsTrue(file, "wps_preview");
            string detail = JoinDetails(FileSize(Field(file, "size")), preview ? "可在线预览" : "");
            var title = Field(file, "name", "附件");
            if (id.Length == 0 || (!directlyDownloadable && !preview))
            {
                Row(title, JoinDetails(detail, "暂无下载"));
                return;
            }
            Row(title, detail);
            BusinessButtons((preview ? "尝试导出" : "下载", BusinessAction(() => QueueAttachment(courseId, assignmentId, id, false)), true),
                ("另存为", BusinessAction(() => QueueAttachment(courseId, assignmentId, id, true)), false));
        });
    }

    private async Task QueueAttachment(string courseId, string? assignmentId, string id, bool alternate)
    {
        string? directory = alternate ? ChooseFolder() : null;
        if (alternate && directory is null) return;
        var payload = new Dictionary<string, object>
        {
            ["source"] = "learning", ["courseId"] = courseId, ["attachmentIds"] = new[] { id },
        };
        if (assignmentId is not null) payload["assignmentId"] = assignmentId;
        if (directory is not null) payload["directory"] = directory;
        await CallAsync("download.enqueue", payload);
        Notice("已加入下载队列");
    }

    private Task ShowDownloads()
    {
        DescribeRoute("downloads");
        downloadsPage = PageVersion;
        SetBusinessRefresh(ShowDownloads);
        int expectedRevision = downloadRevision;
        return ReadBusinessPage("下载记录", () => CallAsync("download.jobs"), data =>
        {
            if (expectedRevision == downloadRevision) currentJobs = data;
            DrawDownloads(currentJobs);
        });
    }

    private void RefreshDownloads(JsonElement snapshot)
    {
        var oldStates = Items(currentJobs, "jobs").ToDictionary(job => Field(job, "id"),
            job => Field(job, "status"), StringComparer.Ordinal);
        currentJobs = snapshot;
        downloadRevision++;
        if (downloadsPage == PageVersion) DrawDownloads(snapshot);
        foreach (var job in Items(snapshot, "jobs"))
        {
            if (!oldStates.TryGetValue(Field(job, "id"), out string? previous) || previous is not ("queued" or "running"))
                continue;
            string status = Field(job, "status");
            if (status == "completed")
            {
                string pageCount = Field(job.Get("result"), "page_count");
                Notice(pageCount.Length == 0 ? "下载完成" : $"已导出 {pageCount} 页 PDF");
            }
            else if (status == "failed") Notice(Field(job.Get("error"), "message", "下载失败"));
        }
    }

    private void DrawDownloads(JsonElement data)
    {
        bool paused = IsTrue(data, "paused");
        BeginPage("下载记录", paused ? "已暂停" : null);
        AddContent(ActionButton(paused ? "继续下载" : "暂停下载",
            BusinessAction(() => UpdateDownloads("download.pause", new { paused = !paused }))));
        DrawItems(Items(data, "jobs"), "暂无下载", job =>
        {
            string status = Field(job, "status");
            string stateLabel = status switch
            {
                "queued" => "等待中", "running" => "正在处理", "completed" => "已完成", "failed" => "失败", _ => status,
            };
            var result = job.Get("result");
            string format = Field(result, "format") switch
            {
                "original" => "原文件", "preview_pdf" => "预览 PDF", "classroom_pdf" => "课堂 PDF", _ => "",
            };
            string pages = Field(result, "page_count");
            string detail = JoinDetails(stateLabel, format, pages.Length == 0 ? "" : pages + " 页",
                Field(job.Get("error"), "message"));
            string id = Field(job, "id");
            Action? action = status switch
            {
                "completed" => BusinessAction(async () => { await CallAsync("desktop.showFile", new { id }); }),
                "failed" => BusinessAction(() => UpdateDownloads("download.retry", new { jobId = id })),
                _ => null,
            };
            Row(Field(job, "title", "下载任务"), detail, action,
                status == "completed" ? "显示位置" : status == "failed" ? "重试" : null);
        });
    }

    private async Task UpdateDownloads(string method, object payload)
    {
        int revision = downloadRevision;
        var result = await CallAsync(method, payload);
        if (revision == downloadRevision) RefreshDownloads(result);
    }

    private Task ShowAccount()
    {
        DescribeRoute("auth");
        SetBusinessRefresh(ShowAccount);
        return ReadBusinessPage("账号与会话", () => CallAsync("auth.status"), DrawAccount,
            error => DrawAccount(default, error.Message));
    }

    private void DrawAccount(JsonElement status) => DrawAccount(status, null);

    private void DrawAccount(JsonElement status, string? error)
    {
        BeginPage("账号与会话");
        if (!string.IsNullOrWhiteSpace(error)) Row("无法更新连接状态", error);
        Section("连接状态", "浙大统一身份认证");
        AccountPlatform(status, "courses", "学在浙大", "learning");
        AccountPlatform(status, "classroom", "智云课堂", "classroom");
        DrawServiceErrors(Items(status, "warnings"));

        Section("登录");
        Row("学号或用户名");
        TextBox username = Input("学号或用户名");
        AddContent(username);
        Row("密码");
        TextBox password = Input("密码", password: true);
        AddContent(password);
        var remember = BusinessCheck("记住账号，自动续登", IsTrue(status, "remembered"));
        if (IsTrue(status, "canRemember")) AddContent(remember);
        else remember.Dispose();
        var actions = new List<(string, Action, bool)>
        {
            ("登录", BusinessAction(() =>
            {
                if (accountRequestPending) return Task.CompletedTask;
                string accountName = username.Text.Trim(), secret = password.Text;
                bool save = !remember.IsDisposed && remember.Checked;
                password.Clear();
                if (accountName.Length == 0 || secret.Length == 0)
                {
                    Notice("请输入统一身份认证账号和密码");
                    return Task.CompletedTask;
                }
                return UpdateAccount("auth.login", new { username = accountName, password = secret, remember = save });
            }), true),
        };
        if (IsTrue(status, "remembered"))
        {
            actions.Add(("使用已记住账号", BusinessAction(() => UpdateAccount("auth.loginSaved")), false));
            actions.Add(("清除记住的账号", BusinessAction(() => UpdateAccount("auth.forget")), false));
        }
        actions.Add(("退出登录", BusinessAction(() => UpdateAccount("auth.logout")), false));
        BusinessButtons(actions.ToArray());
        Section("备用方式", "导入会话");

        var service = new ComboBox
        {
            AccessibleName = "平台", DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Surface, ForeColor = Foreground,
        };
        service.Items.AddRange(["学在浙大", "智云课堂"]);
        service.SelectedIndex = 0;
        AddContent(service);
        Row("Cookie");
        TextBox cookie = Input("Cookie", multiline: true);
        AddContent(cookie);
        AddContent(ActionButton("导入", BusinessAction(() =>
        {
            if (accountRequestPending) return Task.CompletedTask;
            string content = cookie.Text;
            cookie.Clear();
            if (string.IsNullOrWhiteSpace(content))
            {
                Notice("请输入要导入的会话");
                return Task.CompletedTask;
            }
            return UpdateAccount("auth.importCookie", new
            {
                service = service.SelectedIndex == 1 ? "classroom" : "courses",
                cookie = content,
            });
        })));
    }

    private void AccountPlatform(JsonElement status, string key, string title, string officialService)
    {
        var platform = status.Get(key);
        string state = IsTrue(platform, "authenticated") ? "已连接" : Field(platform, "state") == "error"
            ? Field(platform, "message", "检查失败") : "未连接";
        Row(title, state, BusinessAction(async () =>
        {
            await CallAsync("desktop.openOfficial", new { service = officialService });
        }), "官网");
    }

    private async Task UpdateAccount(string method, object? payload = null)
    {
        if (accountRequestPending) return;
        accountRequestPending = true;
        int page = PageVersion;
        try
        {
            var result = await CallAsync(method, payload);
            currentJobs = default;
            downloadRevision++;
            if (page == PageVersion) DrawAccount(result);
            string? message = method switch
            {
                "auth.logout" => "已退出登录",
                "auth.forget" => "已清除记住的账号",
                _ when IsTrue(result.Get("courses"), "authenticated") || IsTrue(result.Get("classroom"), "authenticated")
                    => "已连接浙大服务",
                _ => null,
            };
            if (message is not null) Notice(message);
        }
        finally
        {
            accountRequestPending = false;
        }
    }

    private Task RestoreBusinessPage(JsonElement view)
    {
        string courseId = Field(view, "courseId");
        string assignmentId = Field(view, "assignmentId");
        string tab = Field(view, "tab");
        if (tab is not ("summary" or "homework" or "materials" or "lecture")) tab = "summary";
        return Field(view, "name") switch
        {
            "courses" => ChangePage(ShowCourses),
            "downloads" => ChangePage(ShowDownloads),
            "course" when ValidViewId(courseId) => OpenCourse(courseId, tab),
            "assignment" when ValidViewId(courseId) && ValidViewId(assignmentId) =>
                ChangePage(() => ShowAssignment(courseId, assignmentId)),
            _ => ChangePage(ShowOverview),
        };
    }

    private static bool ValidViewId(string value) =>
        value.Length is > 0 and <= 20 && value[0] is >= '1' and <= '9' && value.All(char.IsAsciiDigit);

    private void SetBusinessRefresh(Func<Task> refresh)
    {
        businessRefresh = refresh;
        businessRefreshPage = PageVersion;
    }

    private async Task<bool> RefreshBusinessPage()
    {
        if (businessRefresh is null || businessRefreshPage != PageVersion) return false;
        await businessRefresh();
        return true;
    }

    private async Task ReadBusinessPage<T>(string title, Func<Task<T>> load, Action<T> render, Action<Exception>? onError = null)
    {
        int page = PageVersion;
        int revision = ++businessReadRevision;
        BeginPage(title);
        Row("正在读取…");
        try
        {
            T data = await load();
            if (page == PageVersion && revision == businessReadRevision && !IsDisposed) render(data);
        }
        catch (Exception error)
        {
            if (page != PageVersion || revision != businessReadRevision || IsDisposed) return;
            if (onError is not null) onError(error);
            else
            {
                BeginPage(title);
                Row("读取未完成", error.Message);
                BusinessButtons(("重试", BusinessAction(async () => { await RefreshBusinessPage(); }), true),
                    ("账号与会话", Go(ShowAccount), false));
            }
        }
    }

    private void DrawServiceErrors(IEnumerable<JsonElement> errors)
    {
        foreach (string message in errors.Select(error => Field(error, "message")).Where(message => message.Length != 0)
                     .Distinct(StringComparer.Ordinal))
            Row("部分内容未更新", message);
    }

    private void BusinessButtons(params (string Text, Action Action, bool Primary)[] actions)
    {
        var group = BusinessBar();
        foreach (var action in actions) group.Controls.Add(SizedBusinessButton(action.Text, action.Action, action.Primary));
        AddContent(group);
    }

    private FlowLayoutPanel BusinessBar() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
        MaximumSize = new Size(ContentWidth, 0), BackColor = Color.Transparent,
    };

    private Button SizedBusinessButton(string label, Action click, bool primary = false)
    {
        var button = ActionButton(label, click, primary);
        button.Width = Math.Min(ContentWidth, TextRenderer.MeasureText(label, button.Font).Width + Dip(20));
        button.Margin = new Padding(0, 0, Dip(4), Dip(4));
        return button;
    }

    private CheckBox BusinessCheck(string label, bool value) => new()
    {
        Text = label, AccessibleName = label, Checked = value, AutoSize = true, BackColor = Surface, ForeColor = Foreground,
        Padding = new Padding(Dip(8), Dip(5), Dip(8), Dip(5)),
    };

    private Action Go(Func<Task> view) => BusinessAction(() => ChangePage(view));

    private Action BusinessAction(Func<Task> operation)
    {
        return async () =>
        {
            try { await operation(); }
            catch (Exception error) { if (!IsDisposed) Notice(error.Message); }
        };
    }

    private void DrawItems(IEnumerable<JsonElement> items, string empty, Action<JsonElement> render)
    {
        bool any = false;
        foreach (var item in items)
        {
            render(item);
            any = true;
        }
        if (!any) Row(empty);
    }

    private static string CourseLabels(JsonElement course) =>
        JoinDetails(Field(course, "instructor"), Field(course, "semester"));

    private static string JoinDetails(params string[] items) =>
        string.Join(" · ", items.Where(item => !string.IsNullOrWhiteSpace(item)));

    private static string LocalDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp.ToOffset(CampusOffset).ToString("M月d日 HH:mm", BusinessCulture) : "";

    private static string FileSize(string value)
    {
        if (!double.TryParse(value, CultureInfo.InvariantCulture, out double amount) ||
            !double.IsFinite(amount) || amount <= 0) return "";
        return amount >= 1024 * 1024 ? $"{amount / (1024 * 1024):0.0} MB" : $"{Math.Max(1, Math.Round(amount / 1024)):0} KB";
    }

    private static IEnumerable<JsonElement> Items(JsonElement value, string key)
    {
        JsonElement array = value.Get(key);
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : Array.Empty<JsonElement>();
    }

    private static bool IsTrue(JsonElement value, string key) => value.Get(key).ValueKind == JsonValueKind.True;

    private static string Field(JsonElement value, string key, string fallback = "")
    {
        string item = Scalar(value.Get(key));
        return string.IsNullOrEmpty(item) ? fallback : item;
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        _ => "",
    };
}

internal static class LearningJson
{
    internal static JsonElement Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
}
