using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RevitDevReload.Ui
{
    public sealed class InvertBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && !b;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && !b;
    }

    // Keeps the log ListBox pinned to the newest line.
    public static class ListBoxBehaviors
    {
        public static readonly DependencyProperty AutoScrollToEndProperty =
            DependencyProperty.RegisterAttached(
                "AutoScrollToEnd", typeof(bool), typeof(ListBoxBehaviors),
                new PropertyMetadata(false, OnAutoScrollChanged));

        public static bool GetAutoScrollToEnd(DependencyObject obj)
            => (bool)obj.GetValue(AutoScrollToEndProperty);

        public static void SetAutoScrollToEnd(DependencyObject obj, bool value)
            => obj.SetValue(AutoScrollToEndProperty, value);

        private static void OnAutoScrollChanged(
            DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ListBox listBox || e.NewValue is not true) return;

            listBox.Loaded += (_, _) =>
            {
                if (listBox.ItemsSource is INotifyCollectionChanged incc)
                {
                    incc.CollectionChanged += (_, args) =>
                    {
                        if (args.Action == NotifyCollectionChangedAction.Add
                            && listBox.Items.Count > 0)
                            listBox.ScrollIntoView(listBox.Items[listBox.Items.Count - 1]);
                    };
                }
            };
        }
    }
}
