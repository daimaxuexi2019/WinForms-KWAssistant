﻿using KWAssistant.Data;
using KWAssistant.Data.Model;
using KWAssistant.Properties;
using KWAssistant.Util;
using NLog;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KWAssistant.Form
{
    public partial class MainForm : System.Windows.Forms.Form
    {
        private readonly Logger _logger;

        private readonly Random _random;

        private CancellationTokenSource _cts;

        private bool _isShowBrowser = false;

        public MainForm()
        {
            InitializeComponent();
            _logger = LogManager.GetCurrentClassLogger();
            _random = new Random();
        }

        #region 主窗体事件
        private void MainForm_Load(object sender, EventArgs e)
        {
            //设置启动窗口的大小
            var screenArea = Screen.GetWorkingArea(this);
            ClientSize = new Size((int)(screenArea.Width * 0.6), (int)(screenArea.Height * 0.6));
            SizeAdaptive();

            InitData();
        }

        private void MainForm_SizeChanged(object sender, EventArgs e)
        {
            SizeAdaptive();
        }

        /// <summary>
        /// 按下F12显示或关闭浏览器窗口。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.F12) return;
            var browser = OwnedForms.FirstOrDefault();
            if (browser == null) return;
            browser.Visible = !browser.Visible;
            _isShowBrowser = browser.Visible;
        }
        #endregion

        #region 自定义方法
        /// <summary>
        /// 初始化关键词列表数据
        /// </summary>
        private void InitData()
        {
            kwTreeView.BeginUpdate();
            foreach (var groups in Global.Groups ?? Enumerable.Empty<Group>())
            {
                var parent = new TreeNode { Text = groups.Name };
                foreach (var keyword in groups.Keywords ?? Enumerable.Empty<string>())
                {
                    var child = new TreeNode { Text = keyword };
                    parent.Nodes.Add(child);
                }

                kwTreeView.Nodes.Add(parent);
            }
            kwTreeView.EndUpdate();
        }

        /// <summary>
        /// 设置任务列表和日志列表的表头宽度
        /// </summary>
        private void SizeAdaptive()
        {
            //设置任务列表中Status列的列宽
            taskListView.Columns[3].Width =
                taskListView.ClientSize.Width - taskListView.Columns[0].Width - taskListView.Columns[1].Width -
                taskListView.Columns[2].Width;

            //设置日志列表中Url列的列宽
            logListView.Columns[3].Width =
                logListView.ClientSize.Width - logListView.Columns[0].Width - logListView.Columns[1].Width -
                logListView.Columns[2].Width - logListView.Columns[4].Width - logListView.Columns[5].Width;
        }

        /// <summary>
        /// 添加项到任务列表
        /// </summary>
        /// <param name="id"></param>
        /// <param name="groupName"></param>
        /// <param name="keyword"></param>
        /// <param name="status"></param>
        private void AddTaskItem(int id, string groupName, string keyword, string status)
        {
            var record = new Record
            {
                Id = id,
                GroupName = groupName,
                Keyword = keyword,
                Status = Resources.toDoStatus
            };
            Global.Tasks.Add(record);
            var item = new ListViewItem(id.ToString());
            item.SubItems.Add(groupName);
            item.SubItems.Add(keyword);
            item.SubItems.Add(status);
            taskListView.Items.Add(item);
        }

        /// <summary>
        /// 将任务列表重置为未执行的状态
        /// </summary>
        private void ResetTasks()
        {
            taskListView.BeginUpdate();
            foreach (var record in Global.Tasks)
            {
                if (record.Status == Resources.toDoStatus) break;
                record.Status = Resources.toDoStatus; //更改任务状态为等待中
                var item = taskListView.Items[record.Id - 1];
                item.SubItems[3] = new ListViewItem.ListViewSubItem(item, record.Status); //更新界面
            }
            taskListView.EndUpdate();
        }

        /// <summary>
        /// 打印日志
        /// </summary>
        /// <param name="record"></param>
        private void AddLogItem(Record record)
        {
            var item = new ListViewItem(record.Id.ToString());
            item.SubItems.Add(record.Keyword);
            item.SubItems.Add(record.Title);
            item.SubItems.Add(record.Url);
            item.SubItems.Add(record.DwellTime);
            item.SubItems.Add(record.Ip);
            if (record.DwellTime == Resources.ignoreTask) item.ForeColor = Color.Blue;
            logListView.Items.Add(item);
        }

        /// <summary>
        /// 停止执行任务，按钮回复默认状态，关闭子窗口
        /// </summary>
        private void EndExecute()
        {
            newTaskButton.Enabled = true;
            clickModeButton.Enabled = true;
            quickModeButton.Enabled = true;
            stopButton.Enabled = false;
            cleanTaskButton.Enabled = true;
            cleanLogButton.Enabled = true;
            loopCheckBox.Enabled = true;
            _cts?.Cancel(); //取消异步task
            OwnedForms.FirstOrDefault()?.Close();   //关闭浏览器
        }

        /// <summary>
        /// 开始执行任务前，启用停止按钮，禁用其他按钮
        /// </summary>
        private void BeginExecute()
        {
            newTaskButton.Enabled = false;
            clickModeButton.Enabled = false;
            quickModeButton.Enabled = false;
            stopButton.Enabled = true;
            cleanTaskButton.Enabled = false;
            cleanLogButton.Enabled = true;
            loopCheckBox.Enabled = true;
        }

        /// <summary>
        /// 判断标题和域名是否符合黑白名单规则
        /// </summary>
        /// <param name="title">要检查的标题</param>
        /// <param name="uri">要检查的uri</param>
        /// <returns>符合则返回true</returns>
        private bool IsLegal(string title, string uri)
        {
            return !Global.BlackList.Any()
                   && !Global.WhiteList.Any(s => title.Contains(s) || uri.Contains(s))
                   || Global.BlackList.Any(s => title.Contains(s) || uri.Contains(s));
        }

        /// <summary>
        /// 以快速模式执行任务
        /// </summary>
        /// <param name="cts"></param>
        /// <returns></returns>
        private async Task ExecuteWithQuickMode(CancellationToken cts)
        {
            const int millisecondDelay = 2000;
            const int timeout = 8000;
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            {
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeout) })
                {
                    client.CreateHeaders(); //添加常见的请求头
                    do
                    {
                        ResetTasks();

                        foreach (var record in Global.Tasks)
                        {
                            taskListView.Items[record.Id - 1].Selected = true; //选中该任务所在的行，突出显示
                            taskListView.EnsureVisible(record.Id - 1); //滚动条划到该行

                            var target = Global.Setting.PageMin;
                            while (target <= Global.Setting.PageMax) //浏览页数
                            {
                                var address = new Uri($"http://www.baidu.com/s?wd={record.Keyword}&pn={(target - 1) * 10}"); //搜索关键字+页数
                                var res = await client.GetAsync(address, cts); //访问链接

                                var results = (await res.Content.ReadAsStringAsync()).Analyze();    //取得搜索结果列表
                                foreach (var result in results)
                                {
                                    if (IsLegal(result.Title, result.PartOfRealUri)) //判断标题和uri是否符合黑白名单规则
                                    {
                                        Debug.WriteLine(
                                            $"{result.Title}, ThreadId: {Thread.CurrentThread.ManagedThreadId}, isPoolThread: {Thread.CurrentThread.IsThreadPoolThread}");
                                        var startTime = Environment.TickCount;
                                        try
                                        {
                                            client.SetReferer(address); //设置Referer，/s?wd=
                                            res = await client.GetAsync(result.Link, cts); //访问link?url=
                                            client.SetReferer(res.RequestMessage.RequestUri); //设置Referer，link?url=
                                            res = await client.GetAsync(res.Headers.Location, cts); //访问真实地址

                                            var endTime = Environment.TickCount;
                                            record.DwellTime = $"{endTime - startTime} ms";
                                        }
                                        catch (TaskCanceledException)
                                        {
                                            var endTime = Environment.TickCount;
                                            if (endTime - startTime >= timeout)
                                                record.DwellTime = Resources.timeoutTip;    //超时
                                            else
                                                throw;
                                        }

                                        record.Url = res.RequestMessage.RequestUri.ToString();  //完整的真实地址
                                        await Task.Delay(millisecondDelay, cts);
                                    }
                                    else
                                    {
                                        record.Url = result.PartOfRealUri;
                                        record.DwellTime = Resources.ignoreTask;
                                    }

                                    record.Title = result.Title;
                                    AddLogItem(record); //打印日志
                                    logListView.EnsureVisible(logListView.Items.Count - 1);
                                }

                                client.ClearReferer();  //清除Referer
                                ++target;
                            }

                            record.Status = Resources.doneStatus; //更改任务状态为已完成
                            var item = taskListView.Items[record.Id - 1];
                            item.SubItems[3] = new ListViewItem.ListViewSubItem(item, record.Status); //更新界面
                        }
                    } while (loopCheckBox.Checked);
                }
            }
        }

        /// <summary>
        /// 以点击模式执行任务
        /// </summary>
        /// <param name="cts"></param>
        /// <returns></returns>
        private async Task ExecuteWithClickMode(CancellationToken cts)
        {
            do
            {
                ResetTasks();

                foreach (var record in Global.Tasks)
                {
                    taskListView.Items[record.Id - 1].Selected = true; //选中该任务所在的行，突出显示
                    taskListView.EnsureVisible(record.Id - 1); //滚动条划到该行

                    var browser = new BrowserForm(visible: _isShowBrowser);
                    AddOwnedForm(browser);
                    await Task.Delay(1000, cts);    //等待初始化Cef

                    await browser.NavigateToAsync("https://www.baidu.com", cts);    //打开百度首页
                    await Task.Delay(_random.Next(Global.Setting.IntervalMin, Global.Setting.IntervalMax) * 1000, cts);  //间隔时间

                    var code = $@"document.getElementById('kw').value = '{record.Keyword}';" +
                               @"document.getElementById('su').click();";    //搜索关键词
                    await browser.ExecuteJsAsync(code, cts);    //搜索关键词

                    var target = Global.Setting.PageMin;
                    await browser.PageTurn(target, cts);    //跳到目标起始页
                    await Task.Delay(_random.Next(Global.Setting.SearchMin, Global.Setting.SearchMax) * 1000, cts);  //搜索停留

                    while (target <= Global.Setting.PageMax)
                    {
                        code = @"var myList = document.querySelectorAll('#content_left div.c-container h3 a');" +
                               @"var myEvent = document.createEvent('HTMLEvents');" +
                               @"myEvent.initEvent('mousedown', true, true);";
                        await browser.ExecuteJsAsync(code, cts); //js获取搜索结果列表，创建 mousedown 事件

                        var results = (await browser.GetSourceAsync()).Analyze();   //取得搜索结果列表
                        foreach (var result in results)
                        {
                            if (IsLegal(result.Title, result.PartOfRealUri)) //判断标题和uri是否符合黑白名单规则
                            {
                                code = $@"myList[{result.Id}].dispatchEvent(myEvent);myList[{result.Id}].click();";
                                await browser.ExecuteJsAsync(code, cts);    //打开页面
                                var dwellTime = _random.Next(Global.Setting.ClickMin, Global.Setting.ClickMax);
                                await Task.Delay(dwellTime * 1000, cts);    //停留时间
                                record.DwellTime = $"{dwellTime} s";
                                browser.OwnedForms.FirstOrDefault()?.Close();   //关闭页面
                            }
                            else
                            {
                                record.DwellTime = Resources.ignoreTask;
                            }

                            record.Title = result.Title;
                            record.Url = result.PartOfRealUri;
                            AddLogItem(record); //打印日志
                            logListView.EnsureVisible(logListView.Items.Count - 1);
                        }
                        if (target != Global.Setting.PageMax)
                        {
                            await browser.NextPage(cts);
                        }

                        ++target;
                    }

                    record.Status = Resources.doneStatus; //更改任务状态为已完成
                    var item = taskListView.Items[record.Id - 1];
                    item.SubItems[3] = new ListViewItem.ListViewSubItem(item, record.Status); //更新界面
                    browser.Close();
                }
            } while (loopCheckBox.Checked);
        }
        #endregion

        #region 菜单栏点击事件
        private void whiteListToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new WhiteListForm { StartPosition = FormStartPosition.CenterParent }
                .ShowDialog();
        }

        private void blackListToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new BlackListForm { StartPosition = FormStartPosition.CenterParent }
                .ShowDialog();
        }

        private void settingToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new SettingForm { StartPosition = FormStartPosition.CenterParent }
                .ShowDialog();
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //todo
        }
        #endregion

        #region 关键词列表右键菜单事件
        private void kwTreeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            //右键点击不同级别的节点，显示不同的快捷菜单
            if (e.Button != MouseButtons.Right) return;
            if (e.Node.Level == 0)
            {
                editToolStripMenuItem.Visible = true;
                addToTaskToolStripMenuItem.Visible = true;
                deleteGroupToolStripMenuItem.Visible = true;
            }
            else if (e.Node.Level == 1)
            {
                editToolStripMenuItem.Visible = false;
                addToTaskToolStripMenuItem.Visible = true;
                deleteGroupToolStripMenuItem.Visible = false;
            }
            kwTreeView.SelectedNode = e.Node;
        }

        private void kwTreeView_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (sender == kwTreeView)
            {
                editToolStripMenuItem.Visible = false;
                addToTaskToolStripMenuItem.Visible = false;
                deleteGroupToolStripMenuItem.Visible = false;

                kwTreeView.SelectedNode = null;
            }
        }

        private void newGroupToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var newGroupForm = new NewGroupForm { StartPosition = FormStartPosition.CenterParent };
            newGroupForm.AddNewGroupEvent += name =>
            {
                Global.Groups.Add(new Group { Name = name });
                //持久化
                new Task(() => { FileUtil.SaveKeywords(Config.KeywordFilePath, Global.Groups); }).Start();
                //更新视图
                kwTreeView.Nodes.Add(name);
            };
            newGroupForm.ShowDialog();
        }

        private void collapseAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            kwTreeView.CollapseAll();
        }

        private void expandAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            kwTreeView.ExpandAll();
        }

        private void editToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var group = Global.Groups[kwTreeView.SelectedNode.Index];
            var editForm = new EditForm { StartPosition = FormStartPosition.CenterParent };
            editForm.LoadGroupEvent += () => group;
            editForm.UpdateGroupEvent += newGroup =>
            {
                var index = Global.Groups.FindIndex(g => g.Name == newGroup.Name);
                Global.Groups[index] = newGroup;
                //持久化
                new Task(() => { FileUtil.SaveKeywords(Config.KeywordFilePath, Global.Groups); }).Start();
                //更新视图数据
                var nodes = kwTreeView.Nodes[index].Nodes;
                nodes.Clear();
                foreach (var newKeyword in newGroup.Keywords)
                {
                    nodes.Add(newKeyword);
                }
            };
            editForm.ShowDialog();
        }

        private void addToTaskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_cts != null)  //执行过程中不允许添加任务
            {
                MessageBox.Show(Resources.cannotAddTask, Resources.tip, MessageBoxButtons.OK);
                return;
            }
            var selectNode = kwTreeView.SelectedNode;
            if (selectNode.Level == 0)  //添加分组到任务列表
            {
                var index = Global.Tasks.Count;
                foreach (TreeNode child in selectNode.Nodes)
                {
                    AddTaskItem(++index, selectNode.Text, child.Text, Resources.toDoStatus);
                }
            }
            else if (selectNode.Level == 1) //添加单个关键词到任务列表
            {
                AddTaskItem(Global.Tasks.Count + 1, selectNode.Parent.Text, selectNode.Text, Resources.toDoStatus);
            }
        }

        private void deleteGroupToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectNode = kwTreeView.SelectedNode;
            if (MessageBox.Show(Resources.deleteTip, Resources.tip, MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                Global.Groups.RemoveAt(selectNode.Index);
                kwTreeView.Nodes.Remove(selectNode);
                new Task(() => { FileUtil.SaveKeywords(Config.KeywordFilePath, Global.Groups); }).Start();
            }
        }
        #endregion

        #region 任务列表按钮点击事件
        private void newTaskButton_Click(object sender, EventArgs e)
        {
            var newTaskForm = new NewTaskForm { StartPosition = FormStartPosition.CenterParent };
            newTaskForm.AddTaskEvent += (id, keyword) => { AddTaskItem(id, "", keyword, Resources.toDoStatus); };
            newTaskForm.ShowDialog();
        }

        private async void clickModeButton_Click(object sender, EventArgs e)
        {
            BeginExecute();

            _cts = new CancellationTokenSource();
            try
            {
                await ExecuteWithClickMode(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Click Mode Cancel");
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
                EndExecute();
                MessageBox.Show(ex.Message, Resources.tip, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            _cts = null;

            EndExecute();
        }

        private async void quickModeButton_Click(object sender, EventArgs e)
        {
            BeginExecute();

            _cts = new CancellationTokenSource();
            try
            {
                await ExecuteWithQuickMode(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Quick Mode Cancel");
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
                EndExecute();
                MessageBox.Show(ex.Message, Resources.tip, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            _cts = null;

            EndExecute();
        }

        private void stopButton_Click(object sender, EventArgs e)
        {
            EndExecute();
        }

        private void cleanTaskButton_Click(object sender, EventArgs e)
        {
            Global.Tasks.Clear();
            taskListView.Items.Clear();
        }

        private void cleanLogButton_Click(object sender, EventArgs e)
        {
            logListView.Items.Clear();
        }
        #endregion
    }
}
