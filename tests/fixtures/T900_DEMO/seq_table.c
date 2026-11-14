/*
 * seq_table.c - 시험 사이트 T900 시퀀스 표. 지어낸 자료
 *
 * 문법 표의 스텝 이름이 모두 한 번 이상 나온다. 시간은 1/100초.
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
        ACS_SEND( LOADING, STATION(0) )
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
        WHEN_ANY2( IN( OFF(DEMO_SEN_4) ), ANY2_ON(DEMO_SEN_1, DEMO_SEN_2), RUN )
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
    OUT_FLICKER_UNTIL_IN( DEMO_LAMP, 50, IN( ON(DEMO_IF_IN1) ) )
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
        WHEN_IF( X_LOW_BATTERY, ERR )
        WHEN_IF_IN( X_BACKWARD, IN( ON(DEMO_SEN_3) ), RUN )
        WHEN_ID( 10, 12, RUN )
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
    WAIT_IF( X_NOT(X_CROSS_IN) )
    WAIT_IF_TO( X_CROSS_OUT, 500, ERR )
    WAIT_IF_IN( X_CROSS_IN_ON, IN( OFF(DEMO_SEN_1) ) )
    WAIT_IF_IN_TO( X_CROSS_OUT_ON, IN( ON(DEMO_SEN_2) ), 500, ERR )
    WAIT_ANY2( IN( ANY_ON(DEMO_SEN_1, DEMO_SEN_2), OFF(DEMO_SEN_3) ), ANY2_ON(DEMO_SEN_4, DEMO_IF_IN1) )
    WAIT_ANY2_TO( IN( OFF(DEMO_SEN_3) ), ANY2_ON(DEMO_SEN_1, DEMO_SEN_2), 1000, ERR )
    WAIT_OUT( IN( OFF(DEMO_CV1_PWR) ) )
    DELAY_PARAM( P_AUTO_START_SEC, SEC )
    WAIT_TO_PARAM( IN( ON(DEMO_SEN_4) ), P_ACS_RETRY_SEC, DSEC, ERR )
    WAIT( NO_IN )
    END_RESTART
    L( ERR )
        END_ERROR( 270 )
SEQ_END

/* ---- 회전 하역 ---- */
SEQ_BEGIN( box_turn_unload )
    OBS_OFF
    AGV_SPEED( LOW )
    AGV_SPEED_MMIN( 15 )
    AGV_TURN( CW,  10, 0, 0, 0, ALIGN )
    AGV_TURN( CCW, 10, 0, 0, 5, NOWAIT, OBS )
    AGV_MOVE( 10 )
    AGV_MOVE( 200, TAPE_GD )
    ACS( ARRIVE, STATION(1) )
    ACS_SEND( UNLOADING, 105 )
    ACS_STATION( 0, 71, 1 )
    STATION_RESET
    OBS_OFF_AUTO
    OBS_ON
    END_RESTART
SEQ_END

/* ---- 왕복. 파라미터·키·스테이션 조건 ---- */
SEQ_BEGIN( shuttle )
    SELECT_BEGIN
        WHEN_IF( X_PARAM_EQ(P_SPEED_HIGH, 30), RUN )
        WHEN_IF( X_PARAM_GE(P_ACC, 10), RUN )
        WHEN_IF( X_PARAM_LE(P_DCC, 40), RUN )
        WHEN_IF( X_KEY(UP), RUN )
        WHEN_IF( X_FORBACK_MODE, RUN )
        WHEN_IF( X_STATION_MOD(0, 2, 1), RUN )
        WHEN_IF( X_STATION_DIV(0, 10, 3), RUN )
        WHEN_IF( X_ALWAYS, DONE )
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
