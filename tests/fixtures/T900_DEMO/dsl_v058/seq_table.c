/*
 * seq_table.c - 시험 사이트 T900 시퀀스 표(코어 v0.58.0 새 형식). 지어낸 자료
 *
 * 새 낱말(AND·ANY·상태 낱말·PARAM_TIME·OUT_SET)과 옛 이름 별칭이 한 번 이상 나온다. 시간은 1/100초.
 */
#include "agv/seq_dsl.h"
#include "site.h"
#include "io_map.h"
#include "seq_custom.h"
#include "act_ids.h"

#ifndef AGV_SEQ_EXPANDED
#define AGV_SEQ_EXPANDED 0
#endif
#if AGV_SEQ_EXPANDED

SEQ_LABELS( DONE, REQ, RUN, ERR, BELT_STOP )

/* ---- 벨트 적재 ---- */
SEQ_BEGIN( belt_load )

    /* 0. 자재가 있으면 그대로 재출발 */
    SELECT_BEGIN
        WHEN( IN( OFF(DEMO_SEN_1, DEMO_SEN_2) ), REQ )
        OTHERWISE( DONE )
    SELECT_END

    L( REQ )
        OUT_ON( DEMO_IF_OUT1 )
        WAIT_TO( IN( ON(DEMO_IF_IN1) ), 3000, ERR )
        BELT_ON( 1, CW, UNTIL( ON(DEMO_SEN_2) ) )
        WAIT_BELTS( 1 )
        DELAY(100)
        GOTO( BELT_STOP )

    L( BELT_STOP )
        BELT_OFF( 1 )
        OUT_OFF( DEMO_IF_OUT1 )
        ACS( LOADING, STATION(0) )
        GOTO( DONE )

    L( ERR )
        OUT_OFF( DEMO_IF_OUT1 )
        END_ERROR( 290 )

    L( DONE )
        OBS_ON
        END_RESTART

SEQ_END

/* ---- 벨트 하역 ---- */
SEQ_BEGIN( belt_unload )
    SELECT_BEGIN
        WHEN( AND( OFF(DEMO_SEN_4), ANY(DEMO_SEN_1, DEMO_SEN_2) ), RUN )
        OTHERWISE( DONE )
    SELECT_END
    L( RUN )
        OUT_PULSE( DEMO_LAMP, 50, 50, 3 )
        BELT_ON( 1, CCW )
        WAIT_STABLE( IN( OFF(DEMO_SEN_1, DEMO_SEN_2) ), 300 )
        BELT_OFF( 1 )
        ACS( UNLOADING, STATION0 )
    L( DONE )
        AGV_FORBACK( TOGGLE )
        END_RESTART
SEQ_END

/* ---- 리프트 올림 ---- */
SEQ_BEGIN( lift_load )
    OUT_FLICKER_UNTIL_IN( DEMO_LAMP, 50, AND( ON(DEMO_IF_IN1), FORWARD ) )
    BELT_ON( LIFT, CW )
    WAIT_STABLE_TO( IN( ON(DEMO_LIFT_TOP) ), 50, UNMATCHED( 2000 ), ERR )
    BELT_OFF( LIFT )
    HANDSHAKE( DEMO_IF_OUT1, DEMO_IF_IN1, 1000, ERR )
    ACS_WAIT_ACK( 500, ERR )
    GOTO( DONE )
    L( ERR )
        BELT_OFF( LIFT )
        ERROR_HOLD( 290, IN( ON(DEMO_SENH_1) ), DONE )
    L( DONE )
        END_STOP
SEQ_END

/* ---- 리프트 내림. 여러 분기 ---- */
SEQ_BEGIN( lift_unload )
    SELECT_BEGIN
        WHEN_STATE( ST_DEMO_READY, 1, RUN )
        WHEN( AND( LOW_BATTERY ), ERR )
        WHEN( AND( BACKWARD, ON(DEMO_SEN_3) ), RUN )
        WHEN_ID( 10, 12, RUN )
        WHEN( AND( ID( 13, 15 ) ), RUN )
        SELECT_END_TO( 3000, ERR )
    SELECT_END
    L( RUN )
        BELT_ON( LIFT, CCW )
        WAIT_TO( IN( ON(DEMO_LIFT_BOT) ), 800, ERR )
        BELT_OFF( LIFT )
        SET_STATE( ST_DEMO_DONE, 1 )
        WAIT_STATE( ST_DEMO_READY, 0 )
        GOTO( DONE )
    L( ERR )
        BELT_OFF( LIFT )
        END_ERROR( 258 )
    L( DONE )
        END_RESTART
SEQ_END

/* ---- 대차. 확장 조건 대기 ---- */
SEQ_BEGIN( cart_load )
    WAIT( AND( NOT(CROSS_IN) ) )
    WAIT_TO( AND( CROSS_OUT ), 500, ERR )
    WAIT( AND( CROSS_IN_WAIT, OFF(DEMO_SEN_1) ) )
    WAIT_TO( AND( CROSS_OUT_WAIT, ON(DEMO_SEN_2) ), 500, ERR )
    WAIT( AND( ANY(DEMO_SEN_1, DEMO_SEN_2), OFF(DEMO_SEN_3), ANY(DEMO_SEN_4, DEMO_IF_IN1) ) )
    WAIT_TO( AND( OFF(DEMO_SEN_3), ANY(DEMO_SEN_1, DEMO_SEN_2), ANY(DEMO_SEN_4, DEMO_IF_IN1) ), 1000, ERR )
    WAIT_OUT( IN( OFF(DEMO_CV1_PWR) ) )
    DELAY_PARAM( P_AUTO_START_SEC, SEC )
    WAIT_TO( IN( ON(DEMO_SEN_4) ), PARAM_TIME( P_ACS_RETRY_SEC, DSEC ), ERR )
    WAIT_TO( AND( ON(DEMO_SEN_4), ANY(DEMO_SEN_1, DEMO_SEN_2) ), PARAM_TIME( AS, SEC ), ERR )
    WAIT_PARAM_TO( IN( ON(DEMO_SEN_4) ), P_ACS_RETRY_SEC, DSEC, ERR )
    WAIT_STABLE( AND( ON(DEMO_SEN_1), ANY(DEMO_SEN_2, DEMO_SEN_3) ), 100 )
    ERROR_HOLD( 281, AND( ON(DEMO_SENH_1) ), ERR )
    WAIT( NO_IN )
    END_RESTART
    L( ERR )
        END_ERROR( 270 )
SEQ_END

/* ---- 회전 하역 ---- */
SEQ_BEGIN( box_turn_unload )
    OBS_OFF
    AGV_SPEED( LOW )
    AGV_SPEED( 15 )
    AGV_SPEED_MMIN( 16 )
    OUT_SET( OUTS( DEMO_LAMP ), NO_OUT )
    AGV_TURN( CW,  10, 0, 0, 0, ALIGN )
    AGV_TURN( CCW, 10, 0, 0, 5, NOWAIT, OBS )
    AGV_MOVE( 10 )
    AGV_MOVE( 200, TAPE_GD )
    ACS( ARRIVE, STATION(1) )
    ACS( UNLOADING, 105 )
    ACS_SEND( UNLOADING, 106 )
    ACS( ARRIVE, STATION(0) + 1 )
    ACS( ARRIVE, AGV_ID )
    ACS_STATION( 0, 71, 1 )
    STATION_RESET
    OBS_OFF_AUTO
    OBS_ON
    END_RESTART
SEQ_END

/* ---- 왕복. 파라미터·키·스테이션 조건 ---- */
SEQ_BEGIN( shuttle )
    SELECT_BEGIN
        WHEN( AND( PARAM( DRS, 30 ) ), RUN )
        WHEN( AND( PARAM_MIN( ACC, 10 ) ), RUN )
        WHEN( AND( PARAM_MAX( DCC, 40 ) ), RUN )
        WHEN( AND( KEY( UP ) ), RUN )
        WHEN( AND( FORBACK_MODE ), RUN )
        WHEN( AND( STATION_MOD( 0, 2, 1 ) ), RUN )
        WHEN( AND( STATION_DIV( 0, 10, 3 ) ), RUN )
        WHEN( AND( ERR_IS( 281 ), ON(DEMO_SEN_1) ), RUN )
        WHEN( AND( NOT( LOW_BATTERY ) ), RUN )
        WHEN( AND( STATION( 1, 70, 75 ) ), RUN )
        WHEN( AND( ALWAYS ), DONE )
    SELECT_END
    L( RUN )
        ACT_START( ACT_DEMO_BELT )
        WAIT_ACT( ACT_DEMO_BELT )
        CALL( CALL_DEMO_BEEP )
        OUT_FLICKER_UNTIL_IN_TO( DEMO_LAMP, 50, IN( ON(DEMO_SEN_1) ), 1000, DONE )
    L( DONE )
        END_RESTART
SEQ_END

SEQ_MAP_BEGIN( site_seqs )
    SEQ_MAP( PF_BELT,   SEQ_LOAD,           belt_load )
    SEQ_MAP( PF_BELT,   SEQ_UNLOAD,         belt_unload )
    SEQ_MAP( PF_LIFT,   SEQ_LOAD,           lift_load )
    SEQ_MAP( PF_LIFT,   SEQ_UNLOAD,         lift_unload )
    SEQ_MAP( PF_CART,   SEQ_CART_LOAD,      cart_load )
    SEQ_MAP( PF_BOX,    SEQ_DEMO_TURN,      box_turn_unload )
    SEQ_MAP( 0,         SEQ_DEMO_SHUTTLE,   shuttle )
SEQ_MAP_END

const uint16_t site_nseqs = sizeof(site_seqs) / sizeof(site_seqs[0]);
#endif /* AGV_SEQ_EXPANDED */
