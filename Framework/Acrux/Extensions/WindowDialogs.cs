using System;
using Acrux.Controls;

namespace Acrux.Extensions
{
    /// <summary>
    /// Space Engineers modal-window extension. The sandbox prohibits Task, so
    /// Task-returning ShowDialog API cannot be exposed here. Observe
    /// Window.Closed for completion; ownership and input isolation are unchanged.
    /// </summary>
    public static class WindowDialogs
    {
        public static void ShowModal(this Window dialog, Window owner)
        {
            if (dialog == null) throw new ArgumentNullException(nameof(dialog));
            dialog.OpenModal(owner);
        }
    }
}
