// 
// Copyright (c) 2004,2005 Jaroslaw Kowalski <jkowalski@users.sourceforge.net>
// 
// 
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without 
// modification, are permitted provided that the following conditions 
// are met:
// 
// * Redistributions of source code must retain the above copyright notice, 
//   this list of conditions and the following disclaimer. 
// 
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution. 
// 
// * Neither the name of the Jaroslaw Kowalski nor the names of its 
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission. 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF 
// THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.Threading;
using System.Windows.Forms;
using System.Text;
using System.IO;
using System.Collections;
using System.Drawing;

using NLog.Viewer.Receivers;
using NLog.Viewer.Configuration;
using NLog.Viewer.UI;

namespace NLog.Viewer
{
    public class LogInstance
    {
        private LogInstanceConfiguration _config;
        private LogEventReceiver _receiver;
        private ListView _listView;
        private TreeView _treeView;
        private MainForm _mainForm;
        private TabPage _tabPage;

        private TreeNode _threadsTreeNode;
        private TreeNode _assembliesTreeNode;
        private TreeNode _classesTreeNode;
        private TreeNode _loggersTreeNode;
        private TreeNode _levelsTreeNode;
        private TreeNode _applicationsTreeNode;
        private TreeNode _machinesTreeNode;
        private TreeNode _filesTreeNode;

        private Hashtable _logger2NodeCache = new Hashtable();
        private Hashtable _level2NodeCache = new Hashtable();
        private Hashtable _thread2NodeCache = new Hashtable();
        private Hashtable _assembly2NodeCache = new Hashtable();
        private Hashtable _class2NodeCache = new Hashtable();
        private Hashtable _application2NodeCache = new Hashtable();
        private Hashtable _machine2NodeCache = new Hashtable();
        private Hashtable _file2NodeCache = new Hashtable();

        private CyclicBuffer _events = new CyclicBuffer(10000);

        delegate void AddTreeNodeDelegate(TreeNode parentNode, TreeNode childNode);
        private AddTreeNodeDelegate _addTreeNodeDelegate;

        public LogInstance(LogInstanceConfiguration config)
        {
            _config = config;
            _receiver = LogReceiverFactory.CreateLogReceiver(config.ReceiverType, config.ReceiverParameters);
            _receiver.Connect(this);
            _addTreeNodeDelegate = new AddTreeNodeDelegate(this.AddTreeNode);
        }

        public LogInstanceConfiguration Config
        {
            get { return _config; }
        }

        public void CreateTab(MainForm form)
        {
            _mainForm = form;

            TabPage page = new TabPage();
            _tabPage = page;
            page.ImageIndex = 1;

            _loggersTreeNode = new TreeNode("Loggers");
            _levelsTreeNode = new TreeNode("Levels");
            _threadsTreeNode = new TreeNode("Threads");
            _assembliesTreeNode = new TreeNode("Assemblies");
            _classesTreeNode = new TreeNode("Classes");
            _applicationsTreeNode = new TreeNode("Applications");
            _machinesTreeNode = new TreeNode("Machines");
            _filesTreeNode = new TreeNode("Files");

            TreeView treeView = new TreeView();
            treeView.Nodes.Add(_loggersTreeNode);
            treeView.Nodes.Add(_levelsTreeNode);
            treeView.Nodes.Add(_threadsTreeNode);
            treeView.Nodes.Add(_assembliesTreeNode);
            treeView.Nodes.Add(_classesTreeNode);
            treeView.Nodes.Add(_filesTreeNode);
            treeView.Nodes.Add(_applicationsTreeNode);
            treeView.Nodes.Add(_machinesTreeNode);

            treeView.Dock = DockStyle.Left;
            treeView.ContextMenu = form.treeContextMenu;

            Splitter splitter = new Splitter();
            splitter.Dock = DockStyle.Left;

            ListView listView = new ListView();
            listView.Dock = DockStyle.Fill;
            listView.View = View.Details;
            listView.FullRowSelect = true;
            listView.GridLines = true;
            listView.Font = new Font("Tahoma", 8);
            listView.MultiSelect = false;
            listView.ListViewItemSorter = new ItemComparer(_config);
            //listView.Sorting = SortOrder.Ascending;
            listView.SelectedIndexChanged += new EventHandler(this.ItemSelected);
            listView.ColumnClick += new ColumnClickEventHandler(this.ColumnClicked);

            _listView = listView;
            _treeView = treeView;

            page.Controls.Add(listView);
            page.Controls.Add(splitter);
            page.Controls.Add(treeView);
            page.Text = Config.Name;

            SetupColumns();
        }

        private void SetupColumns()
        {
            foreach (LogColumn lc in Config.Columns)
            {
                _listView.Columns.Add(lc.Name, lc.Width, HorizontalAlignment.Left);
            }
        }

        private void ColumnClicked(object sender, ColumnClickEventArgs e)
        {
            ColumnHeader column = _listView.Columns[e.Column];
            string columnName = column.Text;

            if (_config.OrderBy == columnName)
            {
                _config.SortAscending = !_config.SortAscending;
            }
            else
            {
                _config.SortAscending = true;
                _config.OrderBy = columnName;
            }

            _listView.ListViewItemSorter = new ItemComparer(_config);
        }

        public void Start()
        {
            _receiver.Start();
        }

        public void Stop()
        {
            _receiver.Stop();
        }

        public bool IsRunning
        {
            get { return _receiver.IsRunning; }
        }

        public TabPage TabPage
        {
            get { return _tabPage; }
        }

        private void ItemSelected(object sender, System.EventArgs e)
        {
            if (_listView.SelectedItems.Count == 1)
            {
                object tag = _listView.SelectedItems[0].Tag;
                LogEvent evt = (LogEvent)tag;
                _mainForm.LogEventSelected(this, evt);
            }
        }

        private TreeNode LogEventAttributeToNode(string attributeValue, TreeNode rootNode, Hashtable cache, char separatorChar)
        {
            if (attributeValue == null)
                return null;

            object o = cache[attributeValue];
            if (o != null)
            {
                return (TreeNode)o;
            }

            TreeNode parentNode;

            string baseName;
            int rightmostDot = -1;
            if (separatorChar != 0)
                rightmostDot = attributeValue.LastIndexOf(separatorChar);
            if (rightmostDot < 0)
            {
                parentNode = rootNode;
                baseName = attributeValue;
            }
            else
            {
                string parentLoggerName = attributeValue.Substring(0, rightmostDot);
                baseName = attributeValue.Substring(rightmostDot + 1);
                parentNode = LogEventAttributeToNode(parentLoggerName, rootNode, cache, separatorChar);
            }

            TreeNode newNode = new TreeNode(baseName);
            cache[attributeValue] = newNode;
            _treeView.Invoke(_addTreeNodeDelegate, new object[] { parentNode, newNode });
            return newNode;
        }

        private void AddTreeNode(TreeNode parentNode, TreeNode childNode)
        {
            parentNode.Nodes.Add(childNode);
            //if (parentNode.Parent == null || parentNode.Parent.Parent == null)
            //    parentNode.Expand();
        }

        public void OnTimer()
        {
        }

        private ListViewItem LogEventToListViewItem(LogEvent logEvent)
        {
            ListViewItem item = new ListViewItem();
            logEvent.ListViewItem = item;
            item.Tag = logEvent;
            item.Text = logEvent.ID.ToString();
            item.SubItems.Add(logEvent.ReceivedTime.ToString());
            item.SubItems.Add(logEvent.Logger);
            item.SubItems.Add(logEvent.Level);
            item.SubItems.Add(logEvent.MessageText);

            if (true)
            {
                switch (logEvent.Level[0])
                {
                    case 'T':
                        item.ForeColor = Color.Gray;
                        break;

                    case 'D':
                        item.ForeColor = Color.Navy;
                        break;

                    case 'I':
                        item.ForeColor = Color.Black;
                        break;

                    case 'W':
                        item.ForeColor = Color.Brown;
                        break;

                    case 'E':
                        item.ForeColor = Color.Red;
                        break;

                    case 'F':
                        item.ForeColor = Color.Orange;
                        break;
                }
            }

            return item;
        }

        private bool TryFilters(LogEvent logEvent)
        {
            return true;
        }

        private static int _globalEventID = 0;

        internal void ProcessLogEvent(LogEvent logEvent)
        {
            logEvent.ID = Interlocked.Increment(ref _globalEventID);

            if (!TryFilters(logEvent))
                return;

            LogEvent removedEvent = (LogEvent)_events.AddAndRemoveLast(logEvent);
            if (removedEvent != null && removedEvent.ListViewItem != null)
            {
                _listView.Items.Remove(removedEvent.ListViewItem);
            }

            ListViewItem item = LogEventToListViewItem(logEvent);

            _listView.Items.Add(item);
            _tabPage.Text = Config.Name + " (" + _events.Count + ")";

            DateTime currentTime = DateTime.Now;

            LogEventAttributeToNode(logEvent.Logger, _loggersTreeNode, _logger2NodeCache, '.');
            LogEventAttributeToNode(logEvent.Level, _levelsTreeNode, _level2NodeCache, (char)0);
            LogEventAttributeToNode(logEvent.SourceAssembly, _assembliesTreeNode, _assembly2NodeCache, (char)0);
            TreeNode node = LogEventAttributeToNode(logEvent.SourceType, _classesTreeNode, _class2NodeCache, '.');
            // LogEventAttributeToNode(logEvent.SourceMethod, node, 
            LogEventAttributeToNode(logEvent.Thread, _threadsTreeNode, _thread2NodeCache, (char)0);
            LogEventAttributeToNode(logEvent.SourceApplication, _applicationsTreeNode, _application2NodeCache, (char)0);
            LogEventAttributeToNode(logEvent.SourceMachine, _machinesTreeNode, _machine2NodeCache, (char)0);
            LogEventAttributeToNode(logEvent.SourceFile, _filesTreeNode, _file2NodeCache, (char)'\\');
            //LogEventAttributeToNode(logEvent.SourceType, _typesTreeNode, _type);
        }
       
        enum SortColumnID
        {
            ID,
            SentTime,
            ReceivedTime,
            Logger,
            Level,
            MessageText,
            StackTrace,
            SourceAssembly,
            SourceType,
            SourceMethod,
            SourceFile,
            SourceMachine,
            SourceApplication,
            SourceLine,
            SourceColumn,
            Thread,
        }

        class ItemComparer : IComparer
        {
            private LogInstanceConfiguration _config;

            public ItemComparer(LogInstanceConfiguration config)
            {
                _config = config;
            }

            public int Compare(object x, object y)
            {
                ListViewItem i1 = (ListViewItem)x;
                ListViewItem i2 = (ListViewItem)y;
                LogEvent le1 = (LogEvent)i1.Tag;
                LogEvent le2 = (LogEvent)i2.Tag;
                int returnValue = 0;

                switch (_config.OrderBy)
                {
                    case "Level":
                        returnValue = String.CompareOrdinal(le1.Level, le2.Level);
                        break;

                    case "Received Time":
                        break;

                    case "Text":
                        returnValue = String.CompareOrdinal(le1.MessageText, le2.MessageText);
                        break;
                    
                    default:
                    case "ID":
                        returnValue = (le1.ID - le2.ID);
                        break;
                }
                if (_config.SortAscending)
                    return returnValue;
                else
                    return -returnValue;
            }
        }
    }
}
