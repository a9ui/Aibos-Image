import { NextRequest, NextResponse } from 'next/server';
import { execFile } from 'child_process';
import path from 'path';
import { guardLocalApiRequest } from '@/lib/localApiGuard';
import { formatDirSet, parseDirSet } from '@/lib/pathSet';

export const dynamic = 'force-dynamic';

/**
 * POST /api/browse
 *
 * Opens the native Windows folder picker and returns one or more selected
 * folder paths. The fallback VBScript picker remains single-folder only.
 */
export async function POST(request: NextRequest) {
  const forbidden = guardLocalApiRequest(request);
  if (forbidden) return forbidden;

  const multi = request.nextUrl.searchParams.get('multi') === '1';
  const psScript = `
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class FolderPicker {
    const uint FOS_PICKFOLDERS = 32;
    const uint FOS_FORCEFILESYSTEM = 64;
    const uint FOS_ALLOWMULTISELECT = 512;
    const uint SIGDN_FILESYSPATH = 0x80058000;

    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog {
        [PreserveSig] int Show(IntPtr hwnd);
        void SetFileTypes(uint c, IntPtr p);
        void SetFileTypeIndex(uint i);
        void GetFileTypeIndex(out uint i);
        void Advise(IntPtr p, out uint c);
        void Unadvise(uint c);
        void SetOptions(uint f);
        void GetOptions(out uint f);
        void SetDefaultFolder([MarshalAs(UnmanagedType.Interface)] object p);
        void SetFolder([MarshalAs(UnmanagedType.Interface)] object p);
        void GetFolder([MarshalAs(UnmanagedType.Interface)] out object p);
        void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out object p);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string s);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string s);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string s);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetResult([MarshalAs(UnmanagedType.Interface)] out object p);
        void AddPlace([MarshalAs(UnmanagedType.Interface)] object p, int f);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string s);
        void Close(int hr);
        void SetClientGuid(ref Guid g);
        void ClearClientData();
        void SetFilter([MarshalAs(UnmanagedType.Interface)] object p);
        void GetResults([MarshalAs(UnmanagedType.Interface)] out object p);
        void GetSelectedItems([MarshalAs(UnmanagedType.Interface)] out object p);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent([MarshalAs(UnmanagedType.Interface)] out object p);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attr);
        void Compare([MarshalAs(UnmanagedType.Interface)] object psi, uint hint, out int order);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemArray {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint count);
        void GetItemAt(uint dwIndex, [MarshalAs(UnmanagedType.Interface)] out object ppsi);
    }

    public static string Pick(bool multi) {
        var type = Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7"));
        var dialog = (IFileOpenDialog)Activator.CreateInstance(type);
        uint options = FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM;
        if (multi) options |= FOS_ALLOWMULTISELECT;
        dialog.SetOptions(options);
        dialog.SetTitle(multi ? "Select image folders" : "Select image folder");
        if (dialog.Show(IntPtr.Zero) != 0) return "";

        var paths = new List<string>();
        if (multi) {
            object itemsObject;
            dialog.GetResults(out itemsObject);
            var items = (IShellItemArray)itemsObject;
            uint count;
            items.GetCount(out count);
            for (uint i = 0; i < count; i++) {
                object itemObject;
                items.GetItemAt(i, out itemObject);
                string itemPath;
                ((IShellItem)itemObject).GetDisplayName(SIGDN_FILESYSPATH, out itemPath);
                if (!String.IsNullOrWhiteSpace(itemPath)) paths.Add(itemPath);
            }
        } else {
            object itemObject;
            dialog.GetResult(out itemObject);
            string itemPath;
            ((IShellItem)itemObject).GetDisplayName(SIGDN_FILESYSPATH, out itemPath);
            if (!String.IsNullOrWhiteSpace(itemPath)) paths.Add(itemPath);
        }

        return String.Join("\\n", paths.ToArray());
    }
}
"@
Write-Output ([FolderPicker]::Pick($${multi ? 'true' : 'false'}))
`.trim();

  const scriptPath = path.join(process.cwd(), 'scripts', 'browse_folder.vbs');
  const psCandidates = [
    'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe',
    'C:\\Windows\\SysWOW64\\WindowsPowerShell\\v1.0\\powershell.exe',
    'powershell',
    'pwsh',
  ];
  const cscriptCandidates = [
    'C:\\Windows\\System32\\cscript.exe',
    'C:\\Windows\\SysWOW64\\cscript.exe',
    'cscript',
  ];

  const tryPs = (exe: string) =>
    new Promise<{ ok: boolean; stdout: string; error?: string }>((resolve) => {
      execFile(
        exe,
        ['-NoProfile', '-NonInteractive', '-Command', psScript],
        { timeout: 60000 },
        (err, stdout) => {
          if (err) resolve({ ok: false, stdout: '', error: String(err) });
          else resolve({ ok: true, stdout: stdout.trim() });
        }
      );
    });

  const tryVbs = (exe: string) =>
    new Promise<{ ok: boolean; stdout: string; error?: string }>((resolve) => {
      execFile(
        exe,
        ['//NoLogo', scriptPath],
        { timeout: 60000 },
        (err, stdout) => {
          if (err) resolve({ ok: false, stdout: '', error: String(err) });
          else resolve({ ok: true, stdout: stdout.trim() });
        }
      );
    });

  const errors: string[] = [];

  for (const exe of psCandidates) {
    const result = await tryPs(exe);
    if (result.ok) {
      const paths = parseDirSet(result.stdout);
      return NextResponse.json({ path: formatDirSet(paths), paths });
    }
    errors.push(`${exe}: ${result.error ?? 'unknown'}`);
  }

  for (const exe of cscriptCandidates) {
    const result = await tryVbs(exe);
    if (result.ok) {
      const paths = parseDirSet(result.stdout);
      return NextResponse.json({ path: formatDirSet(paths), paths });
    }
    errors.push(`${exe}: ${result.error ?? 'unknown'}`);
  }

  return NextResponse.json(
    { path: '', paths: [], error: `Failed to open folder picker.\n${errors.join('\n')}` },
    { status: 500 }
  );
}
