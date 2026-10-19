# TrpgMaps 原声带母带（FLAC）

这是游戏自带背景音乐的**无损母带**，8 首、48 kHz / 立体声 / 16 bit，合计约 144 MB
（每首约 3 分钟）。

## 这个目录是干什么的

- **不进任何安装包**：安装版 / 便携版 / 升级包里只有压缩后的 OGG（约 15 MB，见工程里的
  `audio\`，在发布树里是 `win32\audio\`）。
- 发布时 `make_release.ps1` 会把这个目录**原样复制**到发布树的 `audio-flac\`，
  随源码仓库一起提供 —— 想重新混音、换压缩档位、做视频 BGM 都可以从这里拿原文件。
- 母带目录放在工程目录**之外**（工作区根的 `audio-master\`），所以既不会被 MSBuild 编译，
  也不会被源码包吞进去（源码包里放的是 `win32\audio\` 的 OGG）。

## 目录

```
audio-master\
  战斗\  反攻&终战.flac  圣战.flac  大规模战役.flac  普通&伏击.flac  阴谋&压迫.flac
  日常\  城镇.flac  寺院.flac  野外.flac
```

## 改完母带之后怎么重新出 OGG

在**工程目录**（发布树里叫 `win32\`，本地工作目录叫 `win7版本\`）里执行：

```bat
python scripts\encode_audio.py            :: 增量：只转母带更新过的那些
python scripts\encode_audio.py --force    :: 全部重转
python scripts\encode_audio.py -q 3       :: 换档位（默认 2 约 90 kbps；3 约 110 kbps）
```

脚本默认用本机 `%LOCALAPPDATA%\TrpgAudioTools\ffmpeg.exe`
（从 imageio-ffmpeg 的 wheel 里解出来的 gyan.dev essentials build，带 libvorbis；
也可以用 `--ffmpeg` 或环境变量 `TRPG_FFMPEG` 指定）。

转完记得**重新出包**（`make_release.ps1 -Sign`），否则安装包里的还是旧 OGG。

## 为什么是 OGG 而不是别的

播放器的原生层是 miniaudio + stb_vorbis（见 `native\`），它能解 **WAV / MP3 / FLAC / OGG**，
再往上还有 Windows 自带的解码器兜底（M4A / AAC / WMA）。所以：

| 方案 | 体积（整套） | 能不能播 | 结论 |
| --- | --- | --- | --- |
| FLAC（母带） | 144 MB | 能 | 太大，只作母带 |
| WavPack / ALAC | 约 FLAC ±5% | **不能** | 播放器不认，放弃 |
| Opus 80k | 约 11 MB | **不能**（miniaudio 不带 Opus） | 可惜，放弃 |
| **Ogg Vorbis -q:a 2** | **约 15 MB** | 能（自带解码） | **选它** |
| MP3 128k | 约 22 MB | 能 | 同体积下不如 Vorbis |
| AAC 96k（Media Foundation） | 约 17 MB | 能（系统解码器） | Win7 也支持，但依赖系统组件 |

选 -q:a 2 的依据：实测这套素材在 16 kHz 以上的能量只比原文件低 1~2 dB
（14 kHz 以上几乎一致），属于"设备上听不出来"的范围，体积却只有 1/9。

## 授权

曲目由项目作者制作，随本项目一同发布。转载 / 二次使用请注明出处
（https://gitee.com/starnight-dirt/TrpgMaps）。
