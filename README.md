# VRChat Upload Notification

VRChatのアバター/ワールドのアップロード完了・失敗時に通知音を鳴らすUnity Editorツールです。

## 機能

- アップロード成功時に通知音を再生
- アップロード失敗・ビルドエラー時に通知音を再生
- Windowsトースト通知（通知センターにポップアップ表示）
- カスタム音声ファイルの設定（wav, mp3, ogg, aiff対応）
- 音量調整

## 対応SDK

- VRChat SDK3 (World)
- VRChat SDK3 (Avatar)

## インストール

1. [Releases](../../releases)から最新の`.unitypackage`をダウンロード
2. Unityプロジェクトにインポート

### VCC (VRChat Creator Companion) 経由

準備中

## 使い方

1. Unity メニューから `Tools > VRChat Upload Notification` を開く
2. 通知音を選択（テンプレートまたはカスタム）
3. 音量を調整
4. 必要に応じてWindowsトースト通知を有効化

設定は自動保存されます。

## 設定項目

| 項目 | 説明 |
|------|------|
| 通知を有効にする | 通知機能のON/OFF |
| Windowsトースト通知 | 通知センターへのポップアップ表示 |
| 成功音 | アップロード成功時の音（テンプレート or カスタム） |
| 失敗音 | アップロード失敗時の音（テンプレート or カスタム） |
| 音量 | 各音声の音量（0〜100%） |

## テンプレート音声

### 成功音
- 電子レンジのチン
- 電子音1
- 電子音2

### 失敗音
- 電子音1
- ポヨヨーン
- トランペット

## 動作環境

- Unity 2022.3.x
- Windows 10/11
- VRChat SDK3

## ライセンス

MIT License

## 作者

kokoa
