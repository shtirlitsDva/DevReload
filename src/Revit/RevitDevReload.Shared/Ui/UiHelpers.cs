using System;
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

    // Keeps the log TextBox pinned to the newest line, but leaves an active
    // selection alone so the user can copy text while lines keep arriving.
    public static class TextBoxBehaviors
    {
        public static readonly DependencyProperty AutoScrollToEndProperty =
            DependencyProperty.RegisterAttached(
                "AutoScrollToEnd", typeof(bool), typeof(TextBoxBehaviors),
                new PropertyMetadata(false, OnAutoScrollChanged));

        public static bool GetAutoScrollToEnd(DependencyObject obj)
            => (bool)obj.GetValue(AutoScrollToEndProperty);

        public static void SetAutoScrollToEnd(DependencyObject obj, bool value)
            => obj.SetValue(AutoScrollToEndProperty, value);

        private static void OnAutoScrollChanged(
            DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBox textBox || e.NewValue is not true) return;

            textBox.TextChanged += (_, _) =>
            {
                if (textBox.SelectionLength == 0) textBox.ScrollToEnd();
            };
        }
    }
}
