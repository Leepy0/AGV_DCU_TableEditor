/* manual_table.c - 시험 사이트 T900 수동 조작 표. 지어낸 자료
 * 한 사슬 = MAN 줄 여럿 + MAN_ELSE 한 줄. */
#include "agv/seq_dsl.h"
#include "custom_io.h"
#include "io_map.h"
#include "site.h"

MANUAL_BEGIN( site_manual_rows )
    /* ---- 자동 화면 대기 ---- */
    MAN( PF_BELT,       IDLE, IN( ON(DEMO_MAN_UP) ),      BELT( 1, CW ) )
    MAN( PF_BELT,       IDLE, IN( ON(DEMO_MAN_DOWN) ),    BELT( 1, CCW ) )
    MAN_ELSE( PF_BELT,  IDLE,                             BELT( 1, OFF ) )

    /* ---- 조그 화면 ---- */
    MAN( PF_LIFT,       JOG,  AND( KEY( UP ), ON(DEMO_SEN_1) ), BELT( LIFT, CW ), ON(DEMO_MAN_LAMP) )
    MAN( PF_LIFT,       JOG,  KEY( DOWN ),                BELT( LIFT, CCW ) )
    MAN_ELSE( PF_LIFT,  JOG,                              BELT( LIFT, OFF ), OFF(DEMO_MAN_LAMP) )
    MAN( PF_BOX,        JOG,  AND( ON(DEMO_SEN_1), ANY(DEMO_SEN_2, DEMO_SEN_3) ), ON(DEMO_MAN_LAMP) )
    MAN_ELSE( PF_BOX,   JOG,                              NO_OUT )

    /* ---- 매 틱 ---- */
    MAN_FB_SW( PF_BELT, IN_EXT_9 )      /* 방향 전환 스위치 */
    MAN_OBS_B( PF_LIFT, IN_EXT_10 )
MANUAL_END

const uint16_t site_nmanual_rows = sizeof(site_manual_rows) / sizeof(site_manual_rows[0]);
