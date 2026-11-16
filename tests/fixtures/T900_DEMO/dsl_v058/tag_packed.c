/* tag_packed.c - 압축 태그 표 꼴을 흉내 낸 시험 자료(생성물 자리, 빌드하지 않는다). 지어낸 자료
 * 표 편집기는 이 파일을 읽지도 쓰지도 않는다. 문법 표 4.8절 이름이 여기에만 나온다. */
#include "agv/tag_dsl.h"

TAG_X_BEGIN( site_tag_x )
    TAG_X( X_ALWAYS )
TAG_POOL_END
TAG_IN_BEGIN( site_tag_in )
    TAG_IN( NO_IN )
TAG_POOL_END
TAG_ACT_BEGIN( site_tag_act )
    TAG_ACTS( 0, 0, 0, 0 )
TAG_POOL_END
TAGP_BEGIN( site_tagp )
    TAGP( 1, 0, 0, 0, 0, 0 )
TAG_POOL_END
TAG_RANGES_BEGIN( site_tag_ranges )
    TAG_RANGE_P( 0, 1 )
TAG_POOL_END
TAG_PACKED( site_tags_a, site_tagp, site_tag_x, site_tag_in, site_tag_act )
TAG_PACKED_R( site_tags_b, site_tagp, site_tag_x, site_tag_in, site_tag_act, site_tag_ranges )
