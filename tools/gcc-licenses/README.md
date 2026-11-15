# 휴대용 gcc 라이선스 글

`tools/release.ps1` 이 이 폴더의 글을 묶음의 `gcc\licenses\` 로 복사한다. 글은 원문 그대로다.

| 파일 | 라이선스 | 해당 구성 요소 |
|---|---|---|
| `COPYING3` | GNU GPL v3 (29 June 2007) | gcc 드라이버, cc1, collect2, binutils(as, ld) |
| `COPYING.RUNTIME` | GCC Runtime Library Exception v3.1 (31 March 2009) | libgcc, libgcc_s_seh-1.dll (GPL-3.0 에 붙는 예외) |
| `COPYING.LIB` | GNU LGPL v2.1 (February 1999) | libiconv-2.dll, libintl-8.dll |
| `COPYING3.LIB` | GNU LGPL v3 (29 June 2007) | libgmp, libmpfr, libmpc |
| `COPYING.MinGW-w64-runtime.txt` | mingw-w64 런타임 라이선스 고지 모음 | mingw-w64 런타임(crt·lib)·헤더 |
| `COPYING.winpthreads` | MIT 꼴(mingw-w64 project) | libwinpthread-1.dll |
| `LICENSE.zlib` | zlib | zlib1.dll |
| `LICENSE.zstd` | BSD | libzstd.dll |

## 없는 것

| 구성 요소 | 라이선스 | 상태 |
|---|---|---|
| libisl | MIT | 글을 못 찾았다. 넣지 않았다 |
