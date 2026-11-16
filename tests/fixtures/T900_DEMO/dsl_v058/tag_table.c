/*
 * tag_table.c - 시험 사이트 T900 태그 번호 → 동작 규칙(코어 v0.58.0 새 형식). 지어낸 자료
 *
 * 새 낱말(AND·ANY·상태 낱말·ACS·AGV_SPEED)과 옛 이름 별칭이 한 번 이상 나온다.
 * 위에서 아래로 첫 번째로 맞는 규칙 하나만 실행한다.
 */
#include "agv/tag_dsl.h"
#include "act_ids.h"
#include "io_map.h"
#include "site.h"
#include "tag_custom.h"

#define NO_ACTION       0                   /* 동작 없음. 뒤 규칙을 막는다 */
#define T_FB            DIR_TOGGLE          /* 회전 중 방향 전환 */
#define T_FF            DIR_FWD_AFTER_TURN  /* 회전 뒤 전진 */
#define CALL_T(n)       CALL( CALL_TAG( n ) )

#ifndef AGV_TAG_EXPANDED
#define AGV_TAG_EXPANDED 0
#endif
#if AGV_TAG_EXPANDED
TAG_TABLE_BEGIN( site_tags )

    /* ---- 공통 ---- */
    TAG ( 1,   ANY_PROFILE,  AGV_STOP, AGV_AUTOSTART_PARAM )          /* 정지, 자동 출발 */
    TAG ( 2,   ANY_PROFILE,  AGV_SPEED( LLOW ) )
    TAG ( 3,   ANY_PROFILE,  AGV_SPEED( 20 ) )
    TAG ( 4,   ANY_PROFILE,  AGV_SPEED_MMIN( 21 ) )
    TAG ( 5,   ANY_PROFILE,  AGV_BRANCH( LEFT ) )
    TAG ( 6,   ANY_PROFILE,  AGV_STOP( 30 ), AGV_AUTOSTART( 5 ) )
    TAG ( 8,   ANY_PROFILE,  AGV_STOP( TAPE ) )
    TAG ( 9,   ANY_PROFILE,  AGV_STOP( TAPE_GD ) )
    TAG ( 21,  ANY_PROFILE,  AGV_TURN( CW,  90,  0, 1, 0 ) )
    TAG ( 22,  ANY_PROFILE,  AGV_TURN( CCW, 90,  T_FB, 1, 0, ALIGN ) )
    TAG ( 23,  ANY_PROFILE,  AGV_TURN( CW,  180, T_FF, 1, 15, TAPE, OBS ) )
    TAG ( 27,  ANY_PROFILE,  OBS_OFF )
    TAG ( 28,  ANY_PROFILE,  OBS_ON )   
    
    /* ---- 적재·하역 ---- */
    TAG( 40, PF_BELT,   SEQ_START( LOAD ), AGV_FORBACK( TOGGLE ) )
    TAG( 40, PF_LIFT,   OBS_OFF_AUTO, SEQ_START( TAPE, LOAD ) )
    TAG( 40, PF_CART,   AGV_STOP( FL, FROM_SENSOR( DEMO_SEN_4, 100 ) ), SEQ_START( CART_LOAD ) )
    TAG( 41, PF_BELT,   SEQ_START( 20, UNLOAD ) )
    TAG( 41, PF_LIFT,   SEQ_START( TAPE_GD, UNLOAD ) )
    TAG( 41, PF_BOX,    SEQ_START( DEMO_TURN ) )

    /* ---- 조건 ---- */
    TAG_IF( 50, ANY_PROFILE, AND( FORWARD ),   AGV_TURN( CW,  90, 0, 1, 0 ) )
    TAG_IF( 50, ANY_PROFILE, AND( BACKWARD ),  AGV_TURN( CCW, 90, 0, 1, 0 ) )
    TAG_IF( 51, PF_BELT,     AND( FORBACK_MODE ), AGV_STOP, AGV_FORBACK( TOGGLE ), AGV_WAIT_START )
    TAG(    51, PF_BELT,     AGV_STOP, AGV_WAIT_CANCEL )
    TAG_IF( 52, ANY_PROFILE, AND( LOW_BATTERY ), AGV_SPEED( LOW ) )
    TAG_IF( 53, PF_LIFT,     AND( ID( 10, 12 ) ), ACS( ARRIVE, 115 ) )
    TAG_IF( 53, PF_LIFT,     AND( ID( 13, 19 ) ), ACS( ARRIVE, STATION(0) ) )
    TAG_IF( 54, ANY_PROFILE, AND( STATION( 1, 70, 75 ) ),        ACS( STANDBY, AGV_ID_MINUS(10) ) )
    TAG_IF( 55, ANY_PROFILE, IF_STATION_RUNNING( 1, 80, 80 ),    ACS( STANDBY_RESET, 14 ) )
    TAG_IF( 56, ANY_PROFILE, AND( STATION_MOD( 0, 3, 1 ) ),      ACS( NEXT, TAG_NUM + 1 ) )
    TAG_IF( 57, ANY_PROFILE, AND( STATION_DIV( 0, 10, 7 ) ),     ACS( FINISH, STATION(1) - 2 ) )
    TAG_IF( 58, ANY_PROFILE, AND( TAG_IS( MOD( STATION(0), 3 ) ) ), ACS( LOADING, STATION0 ) )
    TAG_IF( 59, ANY_PROFILE, AND( PARAM( TFB_1, 2 ) ),           STATION_RESET )
    TAG_IF( 60, PF_BOX,      AND( ANY(DEMO_SEN_1, DEMO_SEN_2) ), AGV_STOP )
    TAG(    60, PF_BOX,      NO_ACTION )
    TAG_IF( 61, PF_BOX,      AND( TAG_IS(STATION(2) + 5) ),      AGV_STOP )
    TAG_IF( 62, PF_CART,     AND( ID(20, 25), STATION(0, 70, 70) ), AGV_STOP )
    TAG_IF( 63, PF_CART,     AND( NOT(ID(20, 21)), ON(DEMO_SEN_1), OFF(DEMO_SEN_2) ), AGV_STOP )
    TAG_IF( 64, ANY_PROFILE, AND( FORWARD, PARAM(LDS, 1), OFF(DEMO_SEN_3) ), AGV_STOP )
    TAG_IF( 65, ANY_PROFILE, IF_X( X_STATION_RUNNING(1, 5, 9) ), ACT_START( ACT_DEMO_BLINK ) )
    TAG_IF( 66, ANY_PROFILE, AND( CROSS_IN ),                    CALL( TAGC_DEMO_HORN ) )
    TAG_IF( 67, ANY_PROFILE, AND( CROSS_OUT, CROSS_OUT_WAIT ),   OBS_ON )
    TAG_IF( 67, PF_LIFT,     AND( CROSS_IN_WAIT, ALWAYS ),       OBS_OFF )
    TAG_IF( 68, ANY_PROFILE, AND( PARAM_MIN(ACC, 10), PARAM_MAX(DCC, 40) ), AGV_SPEED( MIN ) )
    TAG_IF( 69, ANY_PROFILE, AND( KEY(UP), ON(DEMO_SENI_1), BACKWARD ), AGV_STOP )

    /* 여러 줄 문장 */
    TAG_IF( 70, ANY_PROFILE,
            AND( ERR_IS(281) ),
            AGV_STOP )                                  /* 오류 281 뒤 */

    /* ---- 범위 ---- */
    TAG_RANGE( 101, 120, PF_BELT, ACS( ARRIVE, STATION(0) + 100 ) )
    TAG_RANGE_IF( 121, 130, PF_LIFT, AND( FORWARD ), AGV_STOP, ACS_SEND( ARRIVE, TAG_NUM - 1 ) )

TAG_TABLE_END

const uint16_t site_ntags = sizeof(site_tags) / sizeof(site_tags[0]);
#endif /* AGV_TAG_EXPANDED */
