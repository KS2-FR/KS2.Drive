using KS2Drive.FS;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KS2Drive.Debug
{
    public partial class DebugView : Form
    {
        private DavFS Host;
        private bool _PauseDequeue;
        private bool PauseDequeue
        {
            get
            {
                lock (PauseDequeueLock)
                {
                    return _PauseDequeue;
                }
            }
            set
            {
                lock (PauseDequeueLock)
                {
                    _PauseDequeue = value;
                }
            }
        }
        private object PauseDequeueLock = new object();

        private CancellationTokenSource TokenSource;
        private CancellationToken Token;
        private Task EventDisplayTask;

        public DebugView(DavFS MFS)
        {
            InitializeComponent();
            Host = MFS;
            Host.DebugMessagePosted += (sender, e) => { DisplayEvent(); };
            listView1.DoubleBuffering(true);
        }

        private void DisplayEvent()
        {
            if (PauseDequeue) return;

            //https://stackoverflow.com/questions/19197376/check-if-task-is-already-running-before-starting-new
            if (EventDisplayTask != null && !EventDisplayTask.IsCompleted) return;

            TokenSource = new CancellationTokenSource();
            Token = TokenSource.Token;

            EventDisplayTask = Task.Factory.StartNew(() =>
            {
                if (listView1.InvokeRequired)
                {
                    listView1.Invoke(new MethodInvoker(delegate { DisplayEventSafe(); }));
                }
                else
                {
                    DisplayEventSafe();
                };
            });
        }

        private void DisplayEventSafe()
        {
            while (Host.DebugMessageQueue.TryDequeue(out DebugMessage DM))
            {
                try
                {
                    Token.ThrowIfCancellationRequested();

                    if (DM.MessageType == 1)
                    {
                        for (int i = listView1.Items.Count - 1; i >= Math.Max(0, listView1.Items.Count - 25); i--)
                        {
                            if (listView1.Items[i].SubItems[0].Text.Equals(DM.OperationId))
                            {
                                listView1.Items[i].SubItems[5].Text = DM.date.ToString("HH:MM:ss:ffff");
                                listView1.Items[i].SubItems[6].Text = DM.Result;
                                listView1.Items[i].SubItems[7].Text = Convert.ToInt32((DM.date - (DateTime)listView1.Items[i].SubItems[4].Tag).TotalMilliseconds).ToString();

                                if (!DM.Result.StartsWith("STATUS_SUCCESS")) listView1.Items[i].ForeColor = Color.Red;
                                break;
                            }
                        }
                    }
                    else
                    {
                        listView1.Items.Add(new ListViewItem(new String[] { DM.OperationId, DM.Handle, DM.Caller, DM.Path, DM.date.ToString("HH:mm:ss:ffff"), "", "", "" }));
                        listView1.Items[listView1.Items.Count - 1].SubItems[4].Tag = DM.date;
                        listView1.Items[listView1.Items.Count - 1].EnsureVisible();
                    }
                }
                catch (OperationCanceledException)
                {
                    //Catch cancellation
                }
                catch( Exception ex)
                {
                    //This could be a real error
                }
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (EventDisplayTask != null && !EventDisplayTask.IsCompleted)
            {
                TokenSource.Cancel();
                EventDisplayTask.Wait();
            }

            Host.DebugMessagePosted -= (ps, pe) => { DisplayEvent(); };
        }

        private void button1_Click(object sender, EventArgs e)
        {
            listView1.Items.Clear();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            if (PauseDequeue)
            {
                button2.Text = "Pause";
                PauseDequeue = false;
                DisplayEvent();
            }
            else
            {
                button2.Text = "Continue";
                PauseDequeue = true;
                TokenSource.Cancel();
                EventDisplayTask.Wait();
            }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            RefreshView();
        }

        private void RefreshView()
        {
            if (listView1.InvokeRequired)
            {
                listView2.Invoke(new MethodInvoker(delegate { RefreshViewSafe(); }));
            }
            else
            {
                RefreshViewSafe();
            };
        }

        private void RefreshViewSafe()
        {
            listView2.Items.Clear();
            foreach (var E in Host.FileNodeCache.OrderBy(x=>x.Key))
            {
                listView2.Items.Add(E.Key);
            }
        }
    }

    public static class ControlExtensions
    {
        public static void DoubleBuffering(this Control control, bool enable)
        {
            var doubleBufferPropertyInfo = control.GetType().GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
            doubleBufferPropertyInfo.SetValue(control, enable, null);
        }
    }
}
