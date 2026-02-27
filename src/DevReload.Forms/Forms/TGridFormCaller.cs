using System;
using System.Collections.Generic;
using System.Linq;

namespace DevReload.Forms
{
    /// <summary>
    /// Static helper for showing a <see cref="TGridForm{T}"/> dialog and
    /// returning the user's selection.
    /// </summary>
    public static class TGridFormCaller
    {
        /// <summary>
        /// Shows a grid of buttons for the given items and returns the selected value.
        /// </summary>
        /// <typeparam name="T">The type of items to choose from.</typeparam>
        /// <param name="items">The items to display.</param>
        /// <param name="displayValue">Function that converts each item to display text.</param>
        /// <param name="message">Prompt message shown above the grid.</param>
        public static T? Call<T>(IEnumerable<T> items, Func<T, string> displayValue, string message)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (displayValue == null) throw new ArgumentNullException(nameof(displayValue));

            var list = items.ToList();
            if (list.Count == 0)
                throw new ArgumentException("Items collection cannot be empty.", nameof(items));

            var form = new TGridForm<T>(list, displayValue, message);
            form.ShowDialog();
            return form.SelectedValue;
        }

        /// <summary>
        /// Shows all values of an enum as selectable buttons and returns the chosen value.
        /// </summary>
        public static T? SelectEnum<T>(string message, IEnumerable<T>? excludeValues = null) where T : struct, Enum
        {
            var values = Enum.GetValues<T>()
                .Where(e => excludeValues == null || !excludeValues.Contains(e))
                .ToList();

            if (values.Count == 0)
                return null;

            var form = new TGridForm<T>(values, value => value.ToString() ?? string.Empty, message);
            form.ShowDialog();
            return form.SelectedValue;
        }
    }
}
