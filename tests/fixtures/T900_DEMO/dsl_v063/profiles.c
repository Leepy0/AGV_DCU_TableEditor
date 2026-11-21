/* profiles.c - 시험 사이트 T900 호기 → 프로파일. 지어낸 자료
 * 위에서 아래로 첫 줄이 맞는다. */
#include "agv/seq_dsl.h"
#include "io_map.h"
#include "site.h"

LOAD_IF( LIF_DEMO_CART, IN( ON(DEMO_SEN_1) ), IN( OFF(DEMO_SEN_2), ON(DEMO_SEN_3) ) )

PROFILE_TABLE_BEGIN( site_profiles )
    PROFILE( STOP_BACK_FL( PF_BELT ), 0, 1, 4 )         /* 호기 1~4: 후진 정지 거리 FL(v0.63.0) */
    PROFILE( PF_BELT,           0,  5,  9  )
    PROFILE_LOAD( PF_LIFT,      0, 10, 19,  LOAD_SENSORS(DEMO_SEN_1, DEMO_SEN_2) )
    PROFILE_LOAD_IF( PF_CART,   0, 20, 29,  LIF_DEMO_CART )     /* 대차 */
    PROFILE( PF_BOX,            0, 30, 39  )
PROFILE_TABLE_END

const uint16_t site_nprofiles = sizeof(site_profiles) / sizeof(site_profiles[0]);
