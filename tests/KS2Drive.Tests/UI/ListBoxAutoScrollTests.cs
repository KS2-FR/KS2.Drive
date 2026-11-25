using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace KS2Drive.Tests.UI
{
    /// <summary>
    /// Auto-scroll of the log list (ListBoxBehavior.ScrollOnNewItem) in a real, displayed window: the list follows new events
    /// like "tail -f", does not jump while the user reads older events, and shows the latest events when the window is shown again.
    /// The window is displayed off-screen.
    /// </summary>
    [TestClass]
    public class ListBoxAutoScrollTests
    {
        private sealed class Harness
        {
            public Window Window;
            public ListView List;
            public ObservableCollection<String> Items = new ObservableCollection<String>(Enumerable.Range(0, 200).Select(i => $"event {i}"));

            public ScrollViewer Viewer => FindScrollViewer(List);

            public bool IsAtEnd => Viewer.VerticalOffset >= Viewer.ScrollableHeight - 1;

            public Harness()
            {
                List = new ListView { Height = 150 };
                ListBoxBehavior.SetScrollOnNewItem(List, true);
                List.ItemsSource = Items;
                Window = new Window { Content = List, Width = 300, Height = 200, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
                Window.Show();
                Flush();
            }

            /// <summary>Processes the pending dispatcher work (layout and background scroll requests).</summary>
            public void Flush()
            {
                for (int i = 0; i < 3; i++)
                {
                    List.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle);
                }
            }

            public void Close() => Window.Close();

            #region Private methods

            private static ScrollViewer FindScrollViewer(DependencyObject Parent)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(Parent); i++)
                {
                    var Child = VisualTreeHelper.GetChild(Parent, i);
                    if (Child is ScrollViewer Viewer) return Viewer;
                    var Found = FindScrollViewer(Child);
                    if (Found != null) return Found;
                }
                return null;
            }

            #endregion
        }

        [TestMethod]
        public void NewEvents_AreFollowed_WhenTheListIsAtTheEnd()
        {
            StaThread.Run(() =>
            {
                var H = new Harness();
                try
                {
                    H.Viewer.ScrollToEnd();
                    H.Flush();

                    for (int i = 0; i < 5; i++)
                    {
                        H.Items.Add($"new {i}");
                        H.Flush();
                    }

                    Assert.IsTrue(H.Viewer.ScrollableHeight > 0, "The list must be scrollable for this test");
                    Assert.IsTrue(H.IsAtEnd, $"Offset {H.Viewer.VerticalOffset} / {H.Viewer.ScrollableHeight}: the last event is not visible");
                    Assert.AreEqual("new 4", H.List.SelectedItem);
                }
                finally
                {
                    H.Close();
                }
            });
        }

        [TestMethod]
        public void NewEvents_DoNotMoveTheList_WhileTheUserReadsOlderEvents()
        {
            StaThread.Run(() =>
            {
                var H = new Harness();
                try
                {
                    H.Viewer.ScrollToTop();
                    H.List.SelectedItem = "event 3";
                    H.Flush();

                    H.Items.Add("new event");
                    H.Flush();

                    Assert.AreEqual(0, H.Viewer.VerticalOffset, "The list jumped to the end while the user was reading");
                    Assert.AreEqual("event 3", H.List.SelectedItem);
                }
                finally
                {
                    H.Close();
                }
            });
        }

        [TestMethod]
        public void EventsAddedWhileHidden_AreShown_WhenTheWindowIsShownAgain()
        {
            StaThread.Run(() =>
            {
                var H = new Harness();
                try
                {
                    H.Viewer.ScrollToTop();
                    H.Flush();
                    H.Window.Hide();
                    for (int i = 0; i < 20; i++) H.Items.Add($"while hidden {i}");

                    H.Window.Show();
                    H.Flush();

                    Assert.IsTrue(H.IsAtEnd, $"Offset {H.Viewer.VerticalOffset} / {H.Viewer.ScrollableHeight}: the latest events are not shown");
                }
                finally
                {
                    H.Close();
                }
            });
        }
    }
}
