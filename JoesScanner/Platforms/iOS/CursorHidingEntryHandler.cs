using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;
using JoesScanner.Views.Controls;

namespace JoesScanner.Platforms.iOS
{
    // iOS-only handler to hide the blinking caret in secure (password) fields without ever revealing the text.
    // We keep IsPassword intact and only hide the caret by making the native UITextField tint transparent.
    public sealed class CursorHidingEntryHandler : EntryHandler
    {
        private UIColor? _originalTint;

        private static readonly IPropertyMapper<IEntry, CursorHidingEntryHandler> Mapper =
            new PropertyMapper<IEntry, CursorHidingEntryHandler>(EntryHandler.Mapper)
            {
                [nameof(Entry.IsPassword)] = MapCursorTint,
                [nameof(CursorHidingEntry.HideCursorWhenPassword)] = MapCursorTint
            };

        public CursorHidingEntryHandler() : base(Mapper)
        {
        }

        protected override void ConnectHandler(MauiTextField platformView)
        {
            base.ConnectHandler(platformView);

            _originalTint = platformView.TintColor;
            UpdateCursorTint(platformView, VirtualView);
        }

        protected override void DisconnectHandler(MauiTextField platformView)
        {
            if (_originalTint != null)
                platformView.TintColor = _originalTint;

            base.DisconnectHandler(platformView);
        }

        private static void MapCursorTint(CursorHidingEntryHandler handler, IEntry entry)
        {
            handler.UpdateCursorTint(handler.PlatformView, entry);
        }

        private void UpdateCursorTint(MauiTextField? platformView, IEntry? entry)
        {
            if (platformView == null || entry == null)
                return;

            var cursorHiding = entry as CursorHidingEntry;
            var hideCursor = cursorHiding?.HideCursorWhenPassword ?? false;

            if (hideCursor && entry.IsPassword)
                platformView.TintColor = UIColor.Clear;
            else
                platformView.TintColor = _originalTint ?? platformView.TintColor;
        }
    }
}
