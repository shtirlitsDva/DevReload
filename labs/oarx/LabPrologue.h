//-----------------------------------------------------------------------------
//----- LabPrologue.h
//-
//- The include prologue every ObjectARX/ObjectDBX translation unit needs.
//-
//- The SDK headers do NOT include <windows.h> themselves — they assume the
//- wizard's StdAfx.h already did. Include them cold and you get a wall of
//- "syntax error: identifier 'RECT' / 'HMENU' / HINSTANCE" from dbole.h,
//- dbgrip.h and dbxEntryPoint.h, because the Win32 types they use in their
//- signatures do not exist yet.
//-
//- The #pragma pack(push, 8) is not cosmetic: the AutoCAD binaries were
//- compiled with 8-byte packing, so every ObjectARX header must be compiled
//- under it or the struct layouts this module sees will not match the ones
//- acad.exe hands it. The pop restores the default for the module's own code,
//- exactly as the wizard's StdAfx.h does.
//-
//- The lab uses this instead of a precompiled header so each module stays two
//- files; _ACRXAPP / _DBXAPP come from ObjectARX2025.props.
//-----------------------------------------------------------------------------
#pragma once

#ifndef _ALLOW_RTCc_IN_STL
#define _ALLOW_RTCc_IN_STL
#endif

#pragma pack(push, 8)
#pragma warning(disable : 4786 4996)

#include <windows.h>
#include <tchar.h>
#include <map>

#if defined(_ACRXAPP)
#include <arxHeaders.h>
#else
#include <dbxHeaders.h>
#include <dbxEntryPoint.h>
#endif

#pragma pack(pop)
