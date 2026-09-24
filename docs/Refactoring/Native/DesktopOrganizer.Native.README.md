# Optional C++ Native Module

Do not introduce C++ just because Win32 exists.

Use a native DLL only for boundaries where it materially improves correctness/maintainability or measured performance:

- WorkerW/Progman host and window subclassing;
- shell COM/PIDL operations;
- thumbnail/icon extraction pipeline;
- message-loop code that becomes significantly simpler in native.

Recommended ABI:

```c
DO_API int do_attach_desktop_window(void* hwnd, DO_Error* error);
DO_API int do_detach_desktop_window(void* hwnd, DO_Error* error);
DO_API int do_resolve_shortcut(const wchar_t* path, DO_ShortcutInfo* out_info, DO_Error* error);
DO_API void do_free(void* p);
```

No STL types, exceptions, or allocator ownership crossing ABI.
