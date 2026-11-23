using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

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
                var itemsSourcePropertyDescriptor = TypeDescriptor.GetProperties(listBox)["ItemsSource"];
                itemsSourcePropertyDescriptor.AddValueChanged(listBox, ListBox_ItemsSourceChanged);
            }
            else
            {
                listBox.Loaded -= ListBox_Loaded;
                listBox.Unloaded -= ListBox_Unloaded;
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
            listBox.Unloaded -= ListBox_Unloaded;
        }

        /// <summary>
        /// Subscribes to collection changes once the list box is loaded.
        /// </summary>
        static void ListBox_Loaded(object sender, RoutedEventArgs e)
        {
            var listBox = (ListBox)sender;
            var incc = listBox.Items as INotifyCollectionChanged;
            if (incc == null) return;
            listBox.Loaded -= ListBox_Loaded;
            Associations[listBox] = new Capture(listBox);
        }

        /// <summary>
        /// Subscription to a list box items source that scrolls to and selects each added item.
        /// Disposing it removes the subscription.
        /// </summary>
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
            /// Scrolls to and selects the first newly added item.
            /// </summary>
            void incc_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
            {
                if (e.Action == NotifyCollectionChangedAction.Add)
                {
                    listBox.ScrollIntoView(e.NewItems[0]);
                    listBox.SelectedItem = e.NewItems[0];
                }
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
