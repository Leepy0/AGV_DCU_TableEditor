/* tag_custom.h - 시험 사이트 T900 태그 CALL 번호. 지어낸 자료 */
#ifndef TAG_CUSTOM_H
#define TAG_CUSTOM_H

#include "agv/seq.h"

#ifndef CALL_TAG_BASE
#define CALL_TAG_BASE 1
#endif
#define CALL_TAG(n)   (CALL_TAG_BASE + (n))

enum {
    TAGC_DEMO_HORN = CALL_TAG_BASE,
    TAGC_COUNT = 1
};

#endif
