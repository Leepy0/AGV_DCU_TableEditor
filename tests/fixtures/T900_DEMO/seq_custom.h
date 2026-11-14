/* seq_custom.h - 시험 사이트 T900 시퀀스 ID, CALL 번호, 상태 ID. 지어낸 자료 */
#ifndef SEQ_CUSTOM_H
#define SEQ_CUSTOM_H

#include "agv/seq.h"

enum {
    SEQ_DEMO_TURN = SEQ_SITE_BASE,      /* 회전 하역 */
    SEQ_DEMO_SHUTTLE
};

enum {
    CALL_DEMO_BEEP = 0,
    CALL_SEQ_COUNT
};

enum {
    ST_DEMO_READY = 0,
    ST_DEMO_DONE
};

extern const seq_def_t   site_seqs[];
extern const uint16_t    site_nseqs;

#endif
