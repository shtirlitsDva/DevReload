using System;
using System.Collections.Generic;
using System.Linq;

namespace DevReload.Forms
{
    /// <summary>
    /// Static helper for showing a <see cref="StringGridForm"/> dialog and
    /// returning the user's selection.
    /// </summary>
    public static class StringGridFormCaller
    {
        /// <summary>
        /// Shows a grid of buttons — one per string — and returns the selected value.
        /// </summary>
        /// <param name="list">The strings to display as selectable buttons.</param>
        /// <param name="message">Prompt message shown above the grid.</param>
        /// <returns>The selected string, or <c>null</c> if cancelled (Escape).</returns>
        public static string Call(IEnumerable<string> list, string message)
        {
            if (list == null || !list.Any())
                throw new ArgumentException("List cannot be null or empty.", nameof(list));
            var form = new StringGridForm(list, message);
            form.ShowDialog();
            return form.SelectedValue;
        }

        /// <summary>Shows a Yes/No grid dialog and returns the boolean result.</summary>
        public static bool YesNo(string message)
        {
            var form = new StringGridForm(
                new List<string> { "Yes", "No" },
                message);
            form.ShowDialog();
            return form.SelectedValue == "Yes";
        }

        /// <summary>
        /// Shows all values of an enum as selectable buttons and returns the chosen value.
        /// </summary>
        public static T? SelectEnum<T>(string message, IEnumerable<T>? excludeValues = null) where T : struct, Enum
        {
            var enumValues = Enum.GetValues<T>()
                .Where(e => excludeValues == null || !excludeValues.Contains(e))
                .Select(e => e.ToString())
                .OrderBy(s => s)
                .ToList();

            var form = new StringGridForm(enumValues, message);
            form.ShowDialog();

            if (string.IsNullOrEmpty(form.SelectedValue))
                return null;

            if (Enum.TryParse<T>(form.SelectedValue, out T result))
                return result;

            return null;
        }
    }
}
