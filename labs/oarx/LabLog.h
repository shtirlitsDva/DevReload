//-----------------------------------------------------------------------------
//----- LabLog.h
//-
//- File-based event log shared by the lab's .arx and .dbx.
//-
//- Why a file and not acutPrintf: the .dbx links only the ObjectDBX libraries
//- (dbx.props), so accore's acutPrintf is not available to it, and the whole
//- point of the lab is that BOTH modules report the same lifecycle events
//- through the same channel. A file is also the only channel a test harness
//- outside AutoCAD can read, which is what makes the load/unload sequence
//- assertable rather than eyeballed.
//-
//- Header-only and self-contained on purpose: each module compiles its own
//- copy, so neither imports a symbol from the other. That decoupling is the
//- property the lab exists to preserve (an .arx that imports from the .dbx
//- pins it, and the dbx then never unmaps).
//-----------------------------------------------------------------------------
#pragma once

#include <windows.h>
#include <string>
#include <cstdio>

namespace oarxlab {

//- %TEMP%\devreload-oarx-lab.log — read by the test harness.
inline std::wstring logPath()
{
    wchar_t tmp[MAX_PATH] = {};
    if (GetTempPathW(MAX_PATH, tmp) == 0)
        return L"devreload-oarx-lab.log";
    return std::wstring(tmp) + L"devreload-oarx-lab.log";
}

//- Append one tab-separated event. Best-effort: a lab logger must never be the
//- reason a load or unload fails, so every failure here is swallowed.
inline void log(const wchar_t* module, const wchar_t* event)
{
    SYSTEMTIME st = {};
    GetLocalTime(&st);
    FILE* f = nullptr;
    if (_wfopen_s(&f, logPath().c_str(), L"a+, ccs=UTF-8") != 0 || f == nullptr)
        return;
    fwprintf(f, L"%04d-%02d-%02d %02d:%02d:%02d.%03d\t%s\t%s\tpid=%lu\tstamp=%d\n",
             st.wYear, st.wMonth, st.wDay,
             st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
             module, event, (unsigned long)GetCurrentProcessId(), (int)LAB_STAMP);
    fclose(f);
}

} // namespace oarxlab
