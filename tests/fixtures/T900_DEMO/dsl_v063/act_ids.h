/* act_ids.h - 시험 사이트 T900 ACT ID. 지어낸 자료. 마지막 줄에 줄 끝 문자가 없다 */
#ifndef ACT_IDS_H
#define ACT_IDS_H

#include "agv/seq.h"

enum {
    ACT_DEMO_BLINK = 0,
    ACT_DEMO_BELT
};

#define ACT_COUNT 2

#include "seq_custom.h"

#endif