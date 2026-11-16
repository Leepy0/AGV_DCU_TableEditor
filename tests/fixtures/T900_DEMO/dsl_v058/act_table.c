/* act_table.c - 시험 사이트 T900 ACT 표. 지어낸 자료 */
#include "agv/seq_dsl.h"
#include "act_ids.h"
#include "io_map.h"
#include "site.h"

#ifndef AGV_SEQ_EXPANDED
#define AGV_SEQ_EXPANDED 0
#endif
#if AGV_SEQ_EXPANDED

ACT_BEGIN( lamp_blink_3x )
    OUT_PULSE( DEMO_LAMP, 30, 30, 3 )
    END_STOP
ACT_END

ACT_BEGIN( belt2_run_5s )
    BELT_ON( 2, CW )
    DELAY( 500 )
    BELT_OFF( 2 )
    END_STOP
ACT_END

ACT_MAP_BEGIN( site_acts )
    ACT_MAP( PF_BELT,   ACT_DEMO_BLINK,     lamp_blink_3x )
    ACT_MAP( 0,         ACT_DEMO_BELT,      belt2_run_5s )
ACT_MAP_END

const uint16_t site_nacts = sizeof(site_acts) / sizeof(site_acts[0]);
#endif /* AGV_SEQ_EXPANDED */
