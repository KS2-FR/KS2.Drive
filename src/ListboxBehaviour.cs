using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace KS2Drive
{
    //From : https://stackoverflow.com/questions/2006729/how-can-i-have-a-listbox-auto-scroll-when-a-new-item-is-added
    /// <summary>
    /// Attached behavior that makes a <see cref="ListBox"/> automatically scroll to (and select)
    /// each item added to its <see cref="INotifyCollectionChanged"/> items source.
    /// </summary>
    public class ListBoxBehavior
    {
        /// <summary>
        /// Active collection-change subscriptions, one per list box that has the behavior enabled.
        /// </summary>
        static readonly Dictionary<ListBox, Capture> Associations = new Dictionary<ListBox, Capture>();

        /// <summary>
        /// Gets the value of the ScrollOnNewItem attached property.
        /// </summary>
        public static bool GetScrollOnNewItem(DependencyObject obj)
        {
            return (bool)obj.GetValue(ScrollOnNewItemProperty);
        }

        /// <summary>
        /// Sets the value of the ScrollOnNewItem attached property.
        /// </summary>
        public static void SetScrollOnNewItem(DependencyObject obj, bool value)
        {
            obj.SetValue(ScrollOnNewItemProperty, value);
        }

        /// <summary>
        /// Attached property enabling auto-scroll to newly added items (default <c>false</c>).
        /// </summary>
        public static readonly DependencyProperty ScrollOnNewItemProperty =
            DependencyProperty.RegisterAttached(
                "ScrollOnNewItem",
                typeof(bool),
                typeof(ListBoxBehavior),
                new UIPropertyMetadata(false, OnScrollOnNewItemChanged));

        /// <summary>
        /// Hooks or unhooks the list box Loaded/Unloaded and ItemsSource change notifications
        /// when the ScrollOnNewItem property is turned on or off.
        /// </summary>
        public static void OnScrollOnNewItemChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            var listBox = d as ListBox;
            if (listBox == null) return;
            bool oldValue = (bool)e.OldValue, newValue = (bool)e.NewValue;
            if (newValue == oldValue) return;
            if (newValue)
            {
                listBox.Loaded += ListBox_Loaded;
                listBox.Unloaded += ListBox_Unloaded;
                listBox.IsVisibleChanged += ListBox_IsVisibleChanged;
                var itemsSourcePropertyDescriptor = TypeDescriptor.GetProperties(listBox)["ItemsSource"];
                itemsSourcePropertyDescriptor.AddValueChanged(listBox, ListBox_ItemsSourceChanged);
            }
            else
            {
                listBox.Loaded -= ListBox_Loaded;
                listBox.Unloaded -= ListBox_Unloaded;
                listBox.IsVisibleChanged -= ListBox_IsVisibleChanged;
                if (Associations.ContainsKey(listBox))
                {
                    Associations[listBox].Dispose();
                    Associations.Remove(listBox); //The static dictionary must not keep the list box alive
                }
                var itemsSourcePropertyDescriptor = TypeDescriptor.GetProperties(listBox)["ItemsSource"];
                itemsSourcePropertyDescriptor.RemoveValueChanged(listBox, ListBox_ItemsSourceChanged);
            }
        }

        /// <summary>
        /// Re-subscribes to the new items source when the list box ItemsSource changes.
        /// </summary>
        private static void ListBox_ItemsSourceChanged(object sender, EventArgs e)
        {
            var listBox = (ListBox)sender;
            if (Associations.ContainsKey(listBox))
                Associations[listBox].Dispose();
            Associations[listBox] = new Capture(listBox);
        }

        /// <summary>
        /// Drops the collection-change subscription when the list box is unloaded and forgets the list box.
        /// </summary>
        static void ListBox_Unloaded(object sender, RoutedEventArgs e)
        {
            var listBox = (ListBox)sender;
            if (Associations.ContainsKey(listBox))
            {
                Associations[listBox].Dispose();
                Associations.Remove(listBox); //The static dictionary must not keep the list box alive
            }
            //Stay subscribed to Loaded/Unloaded: a list box can be unloaded and loaded again (tab, expander...)
        }

        /// <summary>
        /// Subscribes to collection changes once the list box is loaded.
        /// </summary>
        static void ListBox_Loaded(object sender, RoutedEventArgs e)
        {
            var listBox = (ListBox)sender;
            var incc = listBox.Items as INotifyCollectionChanged;
            if (incc == null) return;
            //Replace (and release) any existing subscription, e.g. the one created when ItemsSource was set before loading:
            //otherwise it would stay subscribed forever. Loaded is not unsubscribed so that a reloaded list box subscribes again
            if (Associations.ContainsKey(listBox)) Associations[listBox].Dispose();
            Associations[listBox] = new Capture(listBox);
        }

        /// <summary>
        /// Subscription to a list box items source that scrolls to and selects each added item.
        /// Disposing it removes the subscription.
        /// </summary>
        /// <summary>
        /// Scrolls the list box to its last item (and to the very end, in case the last item is taller than the view).
        /// </summary>
        static void ScrollToLastItem(ListBox listBox)
        {
            if (listBox.Items.Count == 0) return;
            listBox.ScrollIntoView(listBox.Items[listBox.Items.Count - 1]);
            FindScrollViewer(listBox)?.ScrollToEnd();
        }

        /// <summary>
        /// Returns the ScrollViewer of a list box template, or null if the template is not applied yet (e.g. never displayed).
        /// </summary>
        static ScrollViewer FindScrollViewer(DependencyObject Parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(Parent); i++)
            {
                DependencyObject Child = VisualTreeHelper.GetChild(Parent, i);
                if (Child is ScrollViewer Viewer) return Viewer;
                ScrollViewer Found = FindScrollViewer(Child);
                if (Found != null) return Found;
            }
            return null;
        }

        /// <summary>
        /// Scrolls to the last item when the list box becomes visible again: items added while its window was hidden
        /// (the log window lives hidden in the notification area) are then shown, instead of the position at the last display.
        /// </summary>
        static void ListBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var listBox = (ListBox)sender;
            if ((bool)e.NewValue) listBox.Dispatcher.BeginInvoke(new Action(() => ScrollToLastItem(listBox)), DispatcherPriority.Background);
        }

        class Capture : IDisposable
        {
            private readonly ListBox listBox;
            private readonly INotifyCollectionChanged incc;

            /// <summary>
            /// Subscribes to the CollectionChanged event of the list box ItemsSource, if it supports notifications.
            /// </summary>
            public Capture(ListBox listBox)
            {
                this.listBox = listBox;
                incc = listBox.ItemsSource as INotifyCollectionChanged;
                if (incc != null)
                {
                    incc.CollectionChanged += incc_CollectionChanged;
                }
            }

            /// <summary>
            /// Follows the new items like "tail -f": selects the last added item and scrolls to it, unless the user scrolled up
            /// to read older items (the list then stays where it is until the user scrolls back to the end).
            /// </summary>
            void incc_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
            {
                if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null || e.NewItems.Count == 0) return;

                //The viewer already accounts for the new item's height only after the layout pass: compare with the previous end
                ScrollViewer Viewer = FindScrollViewer(listBox);
                bool WasAtEnd = Viewer == null || Viewer.ScrollableHeight <= 0 || Viewer.VerticalOffset >= Viewer.ScrollableHeight - 1;
                if (!WasAtEnd) return;

                object NewItem = e.NewItems[e.NewItems.Count - 1];
                listBox.SelectedItem = NewItem;
                listBox.ScrollIntoView(NewItem);

                //The container of the new item is only generated by the next layout pass (the list is virtualized), so scrolling
                //right now is often ignored: scroll again once the layout is up to date
                listBox.Dispatcher.BeginInvoke(new Action(() => ScrollToLastItem(listBox)), DispatcherPriority.Background);
            }

            /// <summary>
            /// Unsubscribes from the items source CollectionChanged event.
            /// </summary>
            public void Dispose()
            {
                if (incc != null)
                    incc.CollectionChanged -= incc_CollectionChanged;
            }
        }
    }
}
