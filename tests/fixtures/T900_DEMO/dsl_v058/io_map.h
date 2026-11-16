/* io_map.h - 시험 사이트 T900 배선. 지어낸 자료 */
#ifndef IO_MAP_H
#define IO_MAP_H

#include "agv/io.h"

/* ---- 입력 ---- */
#define DEMO_SEN_1      IN_EXT_1
#define DEMO_SEN_2      IN_EXT_2
#define DEMO_SEN_3      IN_EXT_3
#define DEMO_SEN_4      IN_EXT_4
#define DEMO_SENH_1     IN_ACTIVE_HIGH( IN_EXT_5 )      /* 핀 1 활성 */
#define DEMO_LIFT_TOP   IN_EXT_6
#define DEMO_LIFT_BOT   IN_EXT_7
#define DEMO_IF_IN1     IN_EXT_8
#define DEMO_SENI_1     IN_INVERTED( IN_EXT_12 )        /* 반대 극성 */
#define DEMO_SPARE_IN   IO_NONE                         /* 미연결 */

/* ---- 출력 ---- */
#define DEMO_CV1_PWR    OUT_EXT_1
#define DEMO_CV1_DIR    OUT_EXT_2
#define DEMO_CV2_PWR    OUT_EXT_3
#define DEMO_LIFT_UP    OUT_EXT_4
#define DEMO_LIFT_DOWN  OUT_EXT_5
#define DEMO_IF_OUT1    OUT_EXT_6
#define DEMO_LAMP       OUT_LAMP_Y

/* ---- 벨트 ---- */
#define BELT1_CW        OUTS( DEMO_CV1_PWR )
#define BELT1_CCW       OUTS( DEMO_CV1_PWR, DEMO_CV1_DIR )
#define BELT2_CW        OUTS( DEMO_CV2_PWR )
#define BELT2_CCW       NO_OUT
#define BELTLIFT_CW     OUTS( DEMO_LIFT_UP )
#define BELTLIFT_CCW    OUTS( DEMO_LIFT_DOWN )

#endif
