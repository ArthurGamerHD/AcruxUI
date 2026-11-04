using System;
using Acrux.Core.Input;
using Adk.Mvvm;

namespace Acrux.Dialogs
{
    /// <summary>Shared state of a modal dialog's DataContext: title, font size and the request to close.</summary>
    public abstract class DialogViewModel : ObservableObject
    {
        /// <summary>
        /// Translates a toolkit text key (e.g. "FilePicker_Loading") in the host mod's language; null or the key
        /// itself means "use the toolkit's English text".
        /// </summary>
        public Func<string, string> Localizer
        {
            get { return _localizer; }
            set
            {
                if (ReferenceEquals(_localizer, value))
                    return;
                _localizer = value;
                OnLocalizerChanged();
            }
        }

        Func<string, string> _localizer;

        /// <summary>Re-reads localized texts after the host mod set its localizer.</summary>
        protected virtual void OnLocalizerChanged()
        {
        }

        string _title = string.Empty;
        float _fontSize = .65f;
        bool _isCompact;

        protected DialogViewModel()
        {
            CloseCommand = new DelegateCommand(_ => Close());
        }

        public string Title
        {
            get { return _title; }
            set { RaiseAndSetIfChanged(ref _title, value ?? string.Empty, nameof(Title)); }
        }

        public float FontSize
        {
            get { return _fontSize; }
            set
            {
                if (!RaiseAndSetIfChanged(ref _fontSize, value, nameof(FontSize)))
                    return;
                RaisePropertyChanged<float>(nameof(SmallFontSize));
                OnFontSizeChanged();
            }
        }

        public float SmallFontSize => _fontSize * .85f;

        /// <summary>True while the dialog fills a very short screen, where views lay their actions out beside the content.</summary>
        public bool IsCompact
        {
            get { return _isCompact; }
            set
            {
                if (!RaiseAndSetIfChanged(ref _isCompact, value, nameof(IsCompact)))
                    return;
                RaisePropertyChanged<bool>(nameof(IsRegular));
                OnCompactChanged();
            }
        }

        public bool IsRegular => !_isCompact;
        public ICommand CloseCommand { get; }
        public Action CloseRequested { get; set; }

        public void Close()
        {
            var close = CloseRequested;
            if (close != null)
                close();
        }

        protected virtual void OnFontSizeChanged()
        {
        }

        protected virtual void OnCompactChanged()
        {
        }

        public virtual void OnClosed()
        {
        }

        protected string Loc(string key)
        {
            Func<string, string> localize = Localizer;
            string text = null;
            if (localize != null)
            {
                try
                {
                    text = localize(key);
                }
                catch (Exception)
                {
                    text = null;
                }
            }
            if (string.IsNullOrEmpty(text) || string.Equals(text, key, StringComparison.Ordinal))
                text = DialogTexts.English(key);
            return text;
        }

        protected string Loc(string key, object argument)
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc(key), argument);
        }
    }
}
