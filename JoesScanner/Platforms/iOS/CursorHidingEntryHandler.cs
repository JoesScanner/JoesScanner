using CoreGraphics;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;
using JoesScanner.Views.Controls;

namespace JoesScanner.Platforms.iOS
{
    // iOS-only handler that hides the blinking caret in secure (password) fields.
    // Uses a MauiTextField subclass that returns CGRect.Empty from GetCaretRectForPosition
    // when the caret should be hidden — this is reliable across all iOS versions,
    // unlike the TintColor approach which stopped working on newer iOS.
    public sealed class CursorHidingEntryHandler : EntryHandler
    {
        private static readonly IPropertyMapper<IEntry, CursorHidingEntryHandler> CursorMapper =
            new PropertyMapper<IEntry, CursorHidingEntryHandler>(EntryHandler.Mapper)
            {
                [nameof(Entry.IsPassword)] = MapIsPasswordAndCaret,
                [nameof(CursorHidingEntry.HideCursorWhenPassword)] = MapCaretVisibility
            };

        public CursorHidingEntryHandler() : base(CursorMapper)
        {
        }

        protected override MauiTextField CreatePlatformView()
        {
            return new CaretHidingTextField();
        }

        protected override void ConnectHandler(MauiTextField platformView)
        {
            base.ConnectHandler(platformView);
            SyncCaretHidden(platformView, VirtualView);
        }

        private static void MapIsPasswordAndCaret(CursorHidingEntryHandler handler, IEntry entry)
        {
            // Let the base EntryHandler apply SecureTextEntry first.
            EntryHandler.MapIsPassword(handler, entry);
            SyncCaretHidden(handler.PlatformView, entry);
        }

        private static void MapCaretVisibility(CursorHidingEntryHandler handler, IEntry entry)
        {
            SyncCaretHidden(handler.PlatformView, entry);
        }

        private static void SyncCaretHidden(MauiTextField? platformView, IEntry? entry)
        {
            if (platformView is not CaretHidingTextField caretField || entry == null)
                return;

            var cursorHiding = entry as CursorHidingEntry;
            var hideCursor = cursorHiding?.HideCursorWhenPassword ?? false;

            caretField.HideCaret = hideCursor && entry.IsPassword;
        }

        // UITextField subclass that can suppress the blinking caret entirely.
        private sealed class CaretHidingTextField : MauiTextField
        {
            public bool HideCaret { get; set; }

            public override CGRect GetCaretRectForPosition(UITextPosition? position)
            {
                return HideCaret ? CGRect.Empty : base.GetCaretRectForPosition(position);
            }
        }
    }
}
