//-----------------------------------------------------------------------------
//----- LabDbx — minimal ObjectDBX module, the lab's "loads first, unloads last"
//----- half of an ordered module pair.
//-
//- Registers one custom AcDbObject so the unload path actually exercises
//- AcRxDbxApp's deleteAcRxClass sweep rather than unloading an empty module.
//- Every lifecycle message is written to the shared log so a harness outside
//- AutoCAD can assert the order the modules were loaded and unloaded in.
//-
//- Exports LabDbxPing so a deliberately-pinned .arx variant can import it and
//- reproduce the failure mode DevReload has to diagnose: acrxUnloadModule
//- reports success while the file stays mapped because something still
//- references it.
//-----------------------------------------------------------------------------
#include "../LabPrologue.h"
#include "../LabLog.h"

//-----------------------------------------------------------------------------
//- A custom object exists solely to give the unload path real work: its class
//- must be removed from the AcRx runtime tree before the module can unmap, and
//- any instance of it in an open drawing degrades to a proxy. That is exactly
//- the behaviour a reload cycle has to survive.
class LabDbxMarker : public AcDbObject
{
public:
    ACRX_DECLARE_MEMBERS(LabDbxMarker);
    LabDbxMarker() {}
    virtual ~LabDbxMarker() {}
};

ACRX_DXF_DEFINE_MEMBERS(
    LabDbxMarker, AcDbObject,
    AcDb::kDHL_CURRENT, AcDb::kMRelease0,
    AcDbProxyObject::kNoOperation, LABDBXMARKER, LabDbxLab)

//-----------------------------------------------------------------------------
class CLabDbxApp : public AcRxDbxApp
{
public:
    CLabDbxApp() : AcRxDbxApp() {}

    virtual AcRx::AppRetCode On_kInitAppMsg(void* pkt)
    {
        //- The base call is what unlocks the application (m_bUnlocked defaults
        //- to true) and registers the ACRX_DXF_DEFINE_MEMBERS classes. Without
        //- it this module could not be unloaded at all.
        AcRx::AppRetCode retCode = AcRxDbxApp::On_kInitAppMsg(pkt);
        LabDbxMarker::rxInit();
        acrxBuildClassHierarchy();
        oarxlab::log(L"LabDbx", L"kInitAppMsg");
        return retCode;
    }

    virtual AcRx::AppRetCode On_kUnloadAppMsg(void* pkt)
    {
        oarxlab::log(L"LabDbx", L"kUnloadAppMsg");
        deleteAcRxClass(LabDbxMarker::desc());
        return AcRxDbxApp::On_kUnloadAppMsg(pkt);
    }

    virtual void RegisterServerComponents() {}
};

IMPLEMENT_ARX_ENTRYPOINT(CLabDbxApp)

//-----------------------------------------------------------------------------
//- Import bait for the pinned-arx negative test. Nothing in the happy path
//- links against this.
extern "C" __declspec(dllexport) int LabDbxPing()
{
    return LAB_STAMP;
}
