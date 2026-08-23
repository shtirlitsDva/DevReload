//-----------------------------------------------------------------------------
//----- LabArx — minimal ObjectARX module, the lab's "loads last, unloads first"
//----- half of an ordered module pair.
//-
//- Imports NOTHING from LabDbx. That is the property under test: because the
//- .arx holds no symbol reference to the .dbx, unloading the pair leaves no
//- module pinned and both files become writable again. The deliberately-broken
//- counterpart lives in LabArxPinned.
//-
//- LABPING prints the build stamp so a test can prove a reload actually swapped
//- the binary rather than reporting success over the old image.
//-----------------------------------------------------------------------------
#include "../LabPrologue.h"
#include "../LabLog.h"

//- L"..." around a macro that already expands to a narrow literal needs the
//- two-step widen; L##__DATE__ does not compile.
#define LAB_WIDEN2(x) L##x
#define LAB_WIDEN(x) LAB_WIDEN2(x)

//-----------------------------------------------------------------------------
class CLabArxApp : public AcRxArxApp
{
public:
    CLabArxApp() : AcRxArxApp() {}

    virtual AcRx::AppRetCode On_kInitAppMsg(void* pkt)
    {
        //- Base call unlocks the app and registers the ACED_ARXCOMMAND_ENTRY_AUTO
        //- commands below.
        AcRx::AppRetCode retCode = AcRxArxApp::On_kInitAppMsg(pkt);
        oarxlab::log(L"LabArx", L"kInitAppMsg");
        acutPrintf(_T("\n[LabArx] loaded (stamp=%d, built %s %s) — LABPING / LABWHERE\n"),
                   (int)LAB_STAMP, LAB_WIDEN(__DATE__), LAB_WIDEN(__TIME__));
        return retCode;
    }

    virtual AcRx::AppRetCode On_kUnloadAppMsg(void* pkt)
    {
        oarxlab::log(L"LabArx", L"kUnloadAppMsg");
        acutPrintf(_T("\n[LabArx] unloading\n"));
        return AcRxArxApp::On_kUnloadAppMsg(pkt);
    }

    virtual void RegisterServerComponents() {}

    //- Proves the module is live AND which build is live.
    static void OarxLabLabPing()
    {
        oarxlab::log(L"LabArx", L"LABPING");
        acutPrintf(_T("\n[LabArx] LABPING stamp=%d built %s %s\n"),
                   (int)LAB_STAMP, LAB_WIDEN(__DATE__), LAB_WIDEN(__TIME__));
    }

    //- Prints the file this module was mapped from — the reload loop's whole
    //- question is "which copy is resident", and a junction/second-copy mix-up
    //- is exactly the confusion this answers.
    static void OarxLabLabWhere()
    {
        ACHAR path[MAX_PATH] = {};
        HMODULE h = GetModuleHandle(_T("LabArx.arx"));
        if (h != NULL && GetModuleFileName(h, path, MAX_PATH) != 0)
            acutPrintf(_T("\n[LabArx] mapped from %s\n"), path);
        else
            acutPrintf(_T("\n[LabArx] could not resolve its own module handle\n"));
        oarxlab::log(L"LabArx", L"LABWHERE");
    }
};

IMPLEMENT_ARX_ENTRYPOINT(CLabArxApp)

ACED_ARXCOMMAND_ENTRY_AUTO(CLabArxApp, OarxLab, LabPing, _LabPing, ACRX_CMD_MODAL, NULL)
ACED_ARXCOMMAND_ENTRY_AUTO(CLabArxApp, OarxLab, LabWhere, _LabWhere, ACRX_CMD_MODAL, NULL)
