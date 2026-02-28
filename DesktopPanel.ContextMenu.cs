using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        private const int WmInitMenuPopup = 0x0117;
        private const int WmDrawItem = 0x002B;
        private const int WmMeasureItem = 0x002C;
        private const int WmMenuChar = 0x0120;
        private const uint CmdFirst = 1;
        private const uint CmdLast = 0x7FFF;
        private const uint TpmReturnCmd = 0x0100;
        private const uint TpmRightButton = 0x0002;
        private const int SwShowNormal = 1;

        private IContextMenu2? _activeShellContextMenu2;
        private IContextMenu3? _activeShellContextMenu3;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [Flags]
        private enum CmFlags : uint
        {
            Normal = 0x00000000,
            Explore = 0x00000004,
            ExtendedVerbs = 0x00000100
        }

        [Flags]
        private enum CmicFlags : uint
        {
            Unicode = 0x00004000,
            PtInvoke = 0x20000000
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public CmicFlags fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public string? lpParameters;
            public string? lpDirectory;
            public int nShow;
            public int dwHotKey;
            public IntPtr hIcon;
            public string? lpTitle;
            public IntPtr lpVerbW;
            public string? lpParametersW;
            public string? lpDirectoryW;
            public string? lpTitleW;
            public POINT ptInvoke;
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214E6-0000-0000-C000-000000000046")]
        private interface IShellFolder
        {
            [PreserveSig]
            int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

            [PreserveSig]
            int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);

            [PreserveSig]
            int BindToObject(IntPtr pidl, IntPtr pbcReserved, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppvOut);

            [PreserveSig]
            int BindToStorage(IntPtr pidl, IntPtr pbcReserved, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppvObj);

            [PreserveSig]
            int CompareIDs(int lParam, IntPtr pidl1, IntPtr pidl2);

            [PreserveSig]
            int CreateViewObject(IntPtr hwndOwner, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppvOut);

            [PreserveSig]
            int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);

            [PreserveSig]
            int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr rgfReserved, out IntPtr ppv);

            [PreserveSig]
            int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);

            [PreserveSig]
            int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214e4-0000-0000-c000-000000000046")]
        private interface IContextMenu
        {
            [PreserveSig]
            int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, CmFlags uFlags);

            [PreserveSig]
            int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

            [PreserveSig]
            int GetCommandString(UIntPtr idCmd, uint uType, uint pReserved, [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder pszName, uint cchMax);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214f4-0000-0000-c000-000000000046")]
        private interface IContextMenu2
        {
            [PreserveSig]
            int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, CmFlags uFlags);

            [PreserveSig]
            int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

            [PreserveSig]
            int GetCommandString(UIntPtr idCmd, uint uType, uint pReserved, [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder pszName, uint cchMax);

            [PreserveSig]
            int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BCFCE0A0-EC17-11d0-8D10-00A0C90F2719")]
        private interface IContextMenu3
        {
            [PreserveSig]
            int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, CmFlags uFlags);

            [PreserveSig]
            int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

            [PreserveSig]
            int GetCommandString(UIntPtr idCmd, uint uType, uint pReserved, [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder pszName, uint cchMax);

            [PreserveSig]
            int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

            [PreserveSig]
            int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(
            string pszName,
            IntPtr pbc,
            out IntPtr ppidl,
            uint sfgaoIn,
            out uint psfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHBindToParent(
            IntPtr pidl,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv,
            out IntPtr ppidlLast);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint TrackPopupMenuEx(
            IntPtr hmenu,
            uint fuFlags,
            int x,
            int y,
            IntPtr hwnd,
            IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        private void FileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.ListBox listBox)
            {
                return;
            }

            var clickedItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (clickedItem == null)
            {
                return;
            }

            bool toggleModifier = IsToggleModifierPressed();
            bool rangeModifier = IsRangeModifierPressed();

            if (!clickedItem.IsSelected)
            {
                if (!toggleModifier && !rangeModifier)
                {
                    listBox.SelectedItems.Clear();
                }
                clickedItem.IsSelected = true;
            }

            listBox.Focus();
        }

        private void FileList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var clickedItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (clickedItem == null || FileList == null)
            {
                return;
            }

            var selectedItems = FileList.SelectedItems
                .OfType<ListBoxItem>()
                .Where(item => item.Tag is string)
                .ToList();

            if (selectedItems.Count == 0)
            {
                return;
            }

            bool hasOnlyRealItems = selectedItems.All(item =>
                item.Tag is string path &&
                !string.IsNullOrWhiteSpace(path) &&
                !IsParentNavigationItem(item) &&
                (File.Exists(path) || Directory.Exists(path)));

            bool opened = false;
            if (hasOnlyRealItems)
            {
                var realPaths = selectedItems
                    .Select(item => (string)item.Tag)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                opened = TryShowExplorerContextMenu(realPaths);
                if (!opened)
                {
                    opened = ShowWindowsLikeContextMenu(realPaths);
                }
            }

            if (!opened)
            {
                opened = ShowFallbackContextMenu(selectedItems);
            }

            e.Handled = opened;
        }

        private bool ShowWindowsLikeContextMenu(IReadOnlyList<string> realPaths)
        {
            if (realPaths == null || realPaths.Count == 0 || FileList == null)
            {
                return false;
            }

            var menu = new ContextMenu
            {
                PlacementTarget = FileList,
                Placement = PlacementMode.MousePoint
            };

            var openItem = new MenuItem { Header = MainWindow.GetString("Loc.TrayOpen") };
            openItem.Click += (_, _) => OpenPaths(realPaths);
            menu.Items.Add(openItem);

            if (realPaths.Count == 1)
            {
                string onlyPath = realPaths[0];
                var revealItem = new MenuItem { Header = MainWindow.GetString("Loc.ContextRevealInExplorer") };
                revealItem.Click += (_, _) => RevealPathInExplorer(onlyPath);
                menu.Items.Add(revealItem);
            }

            var renameItem = new MenuItem
            {
                Header = MainWindow.GetString("Loc.ContextRename"),
                IsEnabled = CanRenameSelection()
            };
            renameItem.Click += (_, _) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TryBeginRenameSelection();
                }), System.Windows.Threading.DispatcherPriority.Input);
            };
            menu.Items.Add(renameItem);

            menu.Items.Add(new Separator());

            var cutItem = new MenuItem { Header = MainWindow.GetString("Loc.ContextCut") };
            cutItem.Click += (_, _) => TryCopySelectionToClipboard(cut: true);
            menu.Items.Add(cutItem);

            var copyItem = new MenuItem { Header = MainWindow.GetString("Loc.ContextCopy") };
            copyItem.Click += (_, _) => TryCopySelectionToClipboard(cut: false);
            menu.Items.Add(copyItem);

            var pasteItem = new MenuItem
            {
                Header = MainWindow.GetString("Loc.ContextPaste"),
                IsEnabled = CanPasteFileDropFromClipboard()
            };
            pasteItem.Click += (_, _) => TryPasteFromClipboard();
            menu.Items.Add(pasteItem);

            menu.Items.Add(new Separator());

            var deleteItem = new MenuItem { Header = MainWindow.GetString("Loc.PanelsDelete") };
            deleteItem.Click += (_, _) => TryHandleDeleteSelection();
            menu.Items.Add(deleteItem);

            menu.Items.Add(new Separator());

            var moreOptionsItem = new MenuItem { Header = MainWindow.GetString("Loc.ContextMoreOptions") };
            moreOptionsItem.Click += (_, _) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TryShowExplorerContextMenu(realPaths);
                }));
            };
            menu.Items.Add(moreOptionsItem);

            menu.IsOpen = true;
            return true;
        }

        private static bool CanPasteFileDropFromClipboard()
        {
            try
            {
                return System.Windows.Clipboard.ContainsFileDropList();
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        private bool TryShowExplorerContextMenu(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return false;
            }

            if (paths.Count > 1 && !AllPathsShareParent(paths))
            {
                return false;
            }

            IntPtr menuHandle = IntPtr.Zero;
            IntPtr apidl = IntPtr.Zero;
            IntPtr contextMenuPtr = IntPtr.Zero;
            IContextMenu? contextMenu = null;
            IShellFolder? parentFolder = null;
            var absolutePidls = new List<IntPtr>();

            try
            {
                foreach (var path in paths)
                {
                    uint attrs;
                    int parseHr = SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out attrs);
                    if (parseHr != 0 || pidl == IntPtr.Zero)
                    {
                        return false;
                    }

                    absolutePidls.Add(pidl);
                }

                var childPidls = new IntPtr[absolutePidls.Count];
                Guid iidShellFolder = typeof(IShellFolder).GUID;
                for (int i = 0; i < absolutePidls.Count; i++)
                {
                    int bindHr = SHBindToParent(absolutePidls[i], iidShellFolder, out IShellFolder folder, out IntPtr childPidl);
                    if (bindHr != 0)
                    {
                        return false;
                    }

                    if (i == 0)
                    {
                        parentFolder = folder;
                    }
                    else
                    {
                        Marshal.FinalReleaseComObject(folder);
                    }

                    childPidls[i] = childPidl;
                }

                if (parentFolder == null)
                {
                    return false;
                }

                apidl = Marshal.AllocCoTaskMem(IntPtr.Size * childPidls.Length);
                for (int i = 0; i < childPidls.Length; i++)
                {
                    Marshal.WriteIntPtr(apidl, i * IntPtr.Size, childPidls[i]);
                }

                Guid iidContextMenu = typeof(IContextMenu).GUID;
                int hr = parentFolder.GetUIObjectOf(IntPtr.Zero, (uint)childPidls.Length, apidl, iidContextMenu, IntPtr.Zero, out contextMenuPtr);
                if (hr != 0 || contextMenuPtr == IntPtr.Zero)
                {
                    return false;
                }

                contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
                Marshal.Release(contextMenuPtr);
                contextMenuPtr = IntPtr.Zero;

                menuHandle = CreatePopupMenu();
                if (menuHandle == IntPtr.Zero)
                {
                    return false;
                }

                var queryFlags = CmFlags.Normal | CmFlags.Explore;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    queryFlags |= CmFlags.ExtendedVerbs;
                }

                int queryHr = contextMenu.QueryContextMenu(menuHandle, 0, CmdFirst, CmdLast, queryFlags);
                if (queryHr < 0)
                {
                    return false;
                }

                if (!GetCursorPos(out POINT point))
                {
                    return false;
                }

                var helper = new WindowInteropHelper(this);
                _activeShellContextMenu2 = contextMenu as IContextMenu2;
                _activeShellContextMenu3 = contextMenu as IContextMenu3;
                uint selectedCmd = TrackPopupMenuEx(
                    menuHandle,
                    TpmReturnCmd | TpmRightButton,
                    point.X,
                    point.Y,
                    helper.Handle,
                    IntPtr.Zero);

                if (selectedCmd != 0)
                {
                    var command = new CMINVOKECOMMANDINFOEX
                    {
                        cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                        fMask = CmicFlags.Unicode | CmicFlags.PtInvoke,
                        hwnd = helper.Handle,
                        lpVerb = (IntPtr)(selectedCmd - CmdFirst),
                        lpVerbW = (IntPtr)(selectedCmd - CmdFirst),
                        nShow = SwShowNormal,
                        ptInvoke = point
                    };

                    contextMenu.InvokeCommand(ref command);
                    RefreshAfterContextAction();
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                _activeShellContextMenu2 = null;
                _activeShellContextMenu3 = null;

                if (menuHandle != IntPtr.Zero)
                {
                    DestroyMenu(menuHandle);
                }

                if (contextMenu != null)
                {
                    Marshal.FinalReleaseComObject(contextMenu);
                }

                if (contextMenuPtr != IntPtr.Zero)
                {
                    Marshal.Release(contextMenuPtr);
                }

                if (parentFolder != null)
                {
                    Marshal.FinalReleaseComObject(parentFolder);
                }

                if (apidl != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(apidl);
                }

                foreach (var pidl in absolutePidls)
                {
                    if (pidl != IntPtr.Zero)
                    {
                        CoTaskMemFree(pidl);
                    }
                }
            }
        }

        private static bool AllPathsShareParent(IReadOnlyList<string> paths)
        {
            if (paths.Count <= 1)
            {
                return true;
            }

            string baseParent = NormalizeParentPath(paths[0]);
            if (string.IsNullOrWhiteSpace(baseParent))
            {
                return false;
            }

            for (int i = 1; i < paths.Count; i++)
            {
                string parent = NormalizeParentPath(paths[i]);
                if (!string.Equals(baseParent, parent, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static string NormalizeParentPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                string fullPath = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string? parent = Path.GetDirectoryName(fullPath);
                return string.IsNullOrWhiteSpace(parent)
                    ? string.Empty
                    : parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool ShowFallbackContextMenu(IReadOnlyList<ListBoxItem> selectedItems)
        {
            if (selectedItems.Count == 0 || FileList == null)
            {
                return false;
            }

            var realPaths = selectedItems
                .Where(item =>
                    item.Tag is string path &&
                    !string.IsNullOrWhiteSpace(path) &&
                    !IsParentNavigationItem(item) &&
                    (File.Exists(path) || Directory.Exists(path)))
                .Select(item => (string)item.Tag)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool allPseudo = selectedItems.All(item =>
                item.Tag is string path &&
                (string.IsNullOrWhiteSpace(path) ||
                 IsParentNavigationItem(item) ||
                 (!File.Exists(path) && !Directory.Exists(path))));

            var menu = new ContextMenu
            {
                PlacementTarget = FileList,
                Placement = PlacementMode.MousePoint
            };

            if (realPaths.Count > 0)
            {
                var openItem = new MenuItem { Header = MainWindow.GetString("Loc.TrayOpen") };
                openItem.Click += (_, _) => OpenPaths(realPaths);
                menu.Items.Add(openItem);

                if (realPaths.Count == 1)
                {
                    var revealItem = new MenuItem { Header = MainWindow.GetString("Loc.ContextRevealInExplorer") };
                    string onlyPath = realPaths[0];
                    revealItem.Click += (_, _) => RevealPathInExplorer(onlyPath);
                    menu.Items.Add(revealItem);
                }

                var renameItem = new MenuItem
                {
                    Header = MainWindow.GetString("Loc.ContextRename"),
                    IsEnabled = CanRenameSelection()
                };
                renameItem.Click += (_, _) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TryBeginRenameSelection();
                    }), System.Windows.Threading.DispatcherPriority.Input);
                };
                menu.Items.Add(renameItem);
            }

            if (allPseudo)
            {
                var removeItem = new MenuItem { Header = MainWindow.GetString("Loc.ContextRemoveFromPanel") };
                removeItem.Click += (_, _) =>
                {
                    string singleName = GetSelectedItemDisplayName(selectedItems[0]);
                    if (ConfirmDeleteAction(panelOnly: true, selectedItems.Count, singleName) &&
                        RemoveItemsFromPanel(selectedItems))
                    {
                        MainWindow.SaveSettings();
                        MainWindow.NotifyPanelsChanged();
                    }
                };
                menu.Items.Add(removeItem);
            }

            if (realPaths.Count > 0)
            {
                var deleteItem = new MenuItem { Header = MainWindow.GetString("Loc.PanelsDelete") };
                deleteItem.Click += (_, _) => TryHandleDeleteSelection();
                menu.Items.Add(deleteItem);
            }

            if (menu.Items.Count == 0)
            {
                return false;
            }

            menu.IsOpen = true;
            return true;
        }

        private static void OpenPaths(IEnumerable<string> paths)
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(path) ||
                    (!File.Exists(path) && !Directory.Exists(path)))
                {
                    continue;
                }

                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch
                {
                }
            }
        }

        private static void RevealPathInExplorer(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                    return;
                }

                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch
            {
            }
        }

        private void RefreshAfterContextAction()
        {
            if (PanelType == PanelKind.Folder)
            {
                if (!string.IsNullOrWhiteSpace(currentFolderPath) && Directory.Exists(currentFolderPath))
                {
                    LoadFolder(currentFolderPath, saveSettings: false);
                }
                return;
            }

            if (PanelType != PanelKind.List)
            {
                return;
            }

            var filtered = PinnedItems
                .Where(path => !string.IsNullOrWhiteSpace(path) &&
                               (File.Exists(path) || Directory.Exists(path)))
                .ToList();

            bool changed = filtered.Count != PinnedItems.Count;
            LoadList(filtered, saveSettings: false);

            if (changed)
            {
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
            }
        }

        private bool TryHandleShellContextMenuWindowMessage(int msg, IntPtr wParam, IntPtr lParam, ref bool handled, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (_activeShellContextMenu2 == null && _activeShellContextMenu3 == null)
            {
                return false;
            }

            if (msg != WmInitMenuPopup &&
                msg != WmDrawItem &&
                msg != WmMeasureItem &&
                msg != WmMenuChar)
            {
                return false;
            }

            try
            {
                if (_activeShellContextMenu3 != null)
                {
                    _activeShellContextMenu3.HandleMenuMsg2((uint)msg, wParam, lParam, out result);
                    handled = true;
                    return true;
                }

                if (_activeShellContextMenu2 != null)
                {
                    _activeShellContextMenu2.HandleMenuMsg((uint)msg, wParam, lParam);
                    handled = true;
                    result = IntPtr.Zero;
                    return true;
                }
            }
            catch
            {
                handled = false;
                result = IntPtr.Zero;
            }

            return false;
        }
    }
}
