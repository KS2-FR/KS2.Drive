using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace KS2Drive.Tests.UI
{
    [TestClass]
    public class ListBoxBehaviorTests
    {
        private static IDictionary Associations()
        {
            FieldInfo Field = typeof(ListBoxBehavior).GetField("Associations", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(Field, "ListBoxBehavior.Associations not found");
            return (IDictionary)Field.GetValue(null);
        }

        private static void RaiseLoaded(ListBox L) => L.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        private static void RaiseUnloaded(ListBox L) => L.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

        /// <summary>
        /// List box bound to <paramref name="Items"/>, with the behavior enabled after the items source is set, then loaded.
        /// </summary>
        private static ListBox LoadedListBox(ObservableCollection<string> Items)
        {
            var L = new ListBox { ItemsSource = Items };
            ListBoxBehavior.SetScrollOnNewItem(L, true);
            RaiseLoaded(L);
            return L;
        }

        [TestMethod]
        public void Property_IsRegisteredAsAttachedBool()
        {
            Assert.AreEqual("ScrollOnNewItem", ListBoxBehavior.ScrollOnNewItemProperty.Name);
            Assert.AreEqual(typeof(bool), ListBoxBehavior.ScrollOnNewItemProperty.PropertyType);
            Assert.AreEqual(typeof(ListBoxBehavior), ListBoxBehavior.ScrollOnNewItemProperty.OwnerType);
            Assert.AreEqual(false, ListBoxBehavior.ScrollOnNewItemProperty.DefaultMetadata.DefaultValue);
        }

        [TestMethod]
        public void GetSet_DefaultFalse_RoundTrip()
        {
            StaThread.Run(() =>
            {
                var L = new ListBox();
                Assert.IsFalse(ListBoxBehavior.GetScrollOnNewItem(L));
                ListBoxBehavior.SetScrollOnNewItem(L, true);
                Assert.IsTrue(ListBoxBehavior.GetScrollOnNewItem(L));
                Assert.AreEqual(true, L.GetValue(ListBoxBehavior.ScrollOnNewItemProperty));
                ListBoxBehavior.SetScrollOnNewItem(L, false);
                Assert.IsFalse(ListBoxBehavior.GetScrollOnNewItem(L));
            });
        }

        [TestMethod]
        public void Set_OnNonListBox_IsStoredWithoutEffect()
        {
            StaThread.Run(() =>
            {
                var B = new Border();
                ListBoxBehavior.SetScrollOnNewItem(B, true);
                Assert.IsTrue(ListBoxBehavior.GetScrollOnNewItem(B));
                ListBoxBehavior.SetScrollOnNewItem(B, false);
                Assert.IsFalse(ListBoxBehavior.GetScrollOnNewItem(B));
            });
        }

        [TestMethod]
        public void Enabled_NotLoaded_DoesNotSelectNewItems()
        {
            StaThread.Run(() =>
            {
                var Items = new ObservableCollection<string> { "a" };
                var L = new ListBox { ItemsSource = Items };
                ListBoxBehavior.SetScrollOnNewItem(L, true);
                Assert.IsFalse(Associations().Contains(L));
                Items.Add("b");
                Assert.IsNull(L.SelectedItem);
            });
        }

        [TestMethod]
        public void Loaded_SelectsEachNewItem()
        {
            StaThread.Run(() =>
            {
                var Items = new ObservableCollection<string> { "a" };
                var L = LoadedListBox(Items);
                Assert.IsTrue(Associations().Contains(L));
                Assert.IsNull(L.SelectedItem);

                Items.Add("b");
                Assert.AreEqual("b", L.SelectedItem);
                Items.Add("c");
                Assert.AreEqual("c", L.SelectedItem);
                Items.Insert(0, "first");
                Assert.AreEqual("first", L.SelectedItem, "The inserted item is selected wherever it is added");

                ListBoxBehavior.SetScrollOnNewItem(L, false);
            });
        }

        [TestMethod]
        public void Loaded_NonAddChanges_DoNotChangeSelection()
        {
            StaThread.Run(() =>
            {
                var Items = new ObservableCollection<string> { "a", "b", "c" };
                var L = LoadedListBox(Items);
                Items.Remove("c");
                Assert.IsNull(L.SelectedItem);
                Items[0] = "z";
                Assert.IsNull(L.SelectedItem);
                Items.Move(0, 1);
                Assert.IsNull(L.SelectedItem);
                Items.Clear();
                Assert.IsNull(L.SelectedItem);

                ListBoxBehavior.SetScrollOnNewItem(L, false);
            });
        }

        [TestMethod]
        public void Loaded_NotObservableSource_IsAccepted()
        {
            StaThread.Run(() =>
            {
                var L = new ListBox { ItemsSource = new List<string> { "a" } };
                ListBoxBehavior.SetScrollOnNewItem(L, true);
                RaiseLoaded(L); //Must not throw
                Assert.IsNull(L.SelectedItem);
                ListBoxBehavior.SetScrollOnNewItem(L, false);
                Assert.IsFalse(Associations().Contains(L));
            });
        }

        [TestMethod]
        public void LoadedTwice_SubscribesOnlyOnce()
        {
            StaThread.Run(() =>
            {
                var Items = new ObservableCollection<string>();
                var L = LoadedListBox(Items);
                RaiseLoaded(L); //The Loaded handler is removed after the first Loaded event
                RaiseUnloaded(L);
                Assert.IsFalse(Associations().Contains(L));
                Items.Add("x");
                Assert.IsNull(L.SelectedItem, "No subscription must be left after Unloaded");
            });
        }

        [TestMethod]
        public void Disable_UnsubscribesAndCleansDictionary()
        {
            StaThread.Run(() =>
            {
                var Items = new ObservableCollection<string>();
                var L = LoadedListBox(Items);
                Assert.IsTrue(Associations().Contains(L));

                ListBoxBehavior.SetScrollOnNewItem(L, false);
                Assert.IsFalse(Associations().Contains(L));
                Items.Add("x");
                Assert.IsNull(L.SelectedItem);

                //Loaded after disabling does nothing
                RaiseLoaded(L);
                Assert.IsFalse(Associations().Contains(L));
                Items.Add("y");
                Assert.IsNull(L.SelectedItem);
            });
        }

        [TestMethod]
        public void Disable_StopsItemsSourceTracking()
        {
            StaThread.Run(() =>
            {
                var L = new ListBox();
                ListBoxBehavior.SetScrollOnNewItem(L, true);
                ListBoxBehavior.SetScrollOnNewItem(L, false);
                var Items = new ObservableCollection<string>();
                L.ItemsSource = Items;
                Assert.IsFalse(Associations().Contains(L));
                Items.Add("x");
                Assert.IsNull(L.SelectedItem);
            });
        }

        [TestMethod]
        public void SetSameValue_IsANoOp()
        {
            StaThread.Run(() =>
            {
                var Items = new ObservableCollection<string>();
                var L = LoadedListBox(Items);
                ListBoxBehavior.SetScrollOnNewItem(L, true);
                Assert.IsTrue(Associations().Contains(L));
                Items.Add("x");
                Assert.AreEqual("x", L.SelectedItem);
                ListBoxBehavior.SetScrollOnNewItem(L, false);

                var Other = new ListBox();
                ListBoxBehavior.SetScrollOnNewItem(Other, false); //Never enabled: nothing to detach
                Assert.IsFalse(Associations().Contains(Other));
            });
        }

        [TestMethod]
        public void Unloaded_UnsubscribesAndCleansDictionary()
        {
            StaThread.Run(() =>
            {
                var Items = new ObservableCollection<string>();
                var L = LoadedListBox(Items);
                RaiseUnloaded(L);
                Assert.IsFalse(Associations().Contains(L));
                Items.Add("x");
                Assert.IsNull(L.SelectedItem);
                ListBoxBehavior.SetScrollOnNewItem(L, false);
            });
        }

        [TestMethod]
        public void ItemsSourceChanged_FollowsNewSource()
        {
            StaThread.Run(() =>
            {
                var Old = new ObservableCollection<string>();
                var L = LoadedListBox(Old);
                var New = new ObservableCollection<string>();
                L.ItemsSource = New;
                Assert.IsTrue(Associations().Contains(L));

                Old.Add("old");
                Assert.IsNull(L.SelectedItem, "The previous items source must no longer be tracked");
                New.Add("new");
                Assert.AreEqual("new", L.SelectedItem);

                ListBoxBehavior.SetScrollOnNewItem(L, false);
                Assert.IsFalse(Associations().Contains(L));
                New.Add("after");
                Assert.AreEqual("new", L.SelectedItem);
            });
        }

        [TestMethod]
        public void ItemsSourceSetAfterEnabling_ThenUnloaded_LeavesNoSubscription()
        {
            StaThread.Run(() =>
            {
                var Items = new ObservableCollection<string>();
                var L = new ListBox();
                ListBoxBehavior.SetScrollOnNewItem(L, true);
                L.ItemsSource = Items;   //Typical XAML order: subscription created by the ItemsSource change
                RaiseLoaded(L);
                RaiseUnloaded(L);
                Assert.IsFalse(Associations().Contains(L));
                Items.Add("x");
                Assert.IsNull(L.SelectedItem, "No subscription must survive Unloaded");
                ListBoxBehavior.SetScrollOnNewItem(L, false);
            });
        }

        [TestMethod]
        public void ItemsSourceSetAfterEnabling_ThenDisabled_LeavesNoSubscription()
        {
            StaThread.Run(() =>
            {
                var Items = new ObservableCollection<string>();
                var L = new ListBox();
                ListBoxBehavior.SetScrollOnNewItem(L, true);
                L.ItemsSource = Items;
                RaiseLoaded(L);
                ListBoxBehavior.SetScrollOnNewItem(L, false);
                Items.Add("x");
                Assert.IsNull(L.SelectedItem, "No subscription must survive disabling the behavior");
            });
        }

        [TestMethod]
        public void ReloadedAfterUnload_StillSelectsNewItems()
        {
            StaThread.Run(() =>
            {
                //A list box in a TabControl or an Expander is unloaded and loaded again when it is hidden and shown
                var Items = new ObservableCollection<string>();
                var L = LoadedListBox(Items);
                RaiseUnloaded(L);
                RaiseLoaded(L);
                Items.Add("x");
                Assert.AreEqual("x", L.SelectedItem, "The behavior is still enabled: it must work again after the list box is reloaded");
                ListBoxBehavior.SetScrollOnNewItem(L, false);
            });
        }

        [TestMethod]
        public void SeveralListBoxes_AreIndependent()
        {
            StaThread.Run(() =>
            {
                var Items1 = new ObservableCollection<string>();
                var Items2 = new ObservableCollection<string>();
                var L1 = LoadedListBox(Items1);
                var L2 = LoadedListBox(Items2);
                Items1.Add("one");
                Assert.AreEqual("one", L1.SelectedItem);
                Assert.IsNull(L2.SelectedItem);

                RaiseUnloaded(L1);
                Assert.IsFalse(Associations().Contains(L1));
                Assert.IsTrue(Associations().Contains(L2));
                Items2.Add("two");
                Assert.AreEqual("two", L2.SelectedItem);
                ListBoxBehavior.SetScrollOnNewItem(L1, false);
                ListBoxBehavior.SetScrollOnNewItem(L2, false);
            });
        }
    }
}
