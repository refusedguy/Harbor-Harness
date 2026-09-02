# Remote: Harbor поверх Tailscale

Harbor-daemon можно держать на домашней машине (Dell, NAS, любой always-on box) и
подключаться к нему **из любой точки мира** — телефон на мобильном интернете,
ноутбук в офисе, VPS — без проброса портов, белого IP и без собственного
шифрования. Сеть делает Tailscale (WireGuard), Harbor добавляет поверх второй
фактор: PSK-handshake из QR-пейринга.

```
телефон (tailnet) ──┐
ноутбук (tailnet) ──┼──► tailscale0 ──► harbor daemon (Dell) ──► UDS/агенты
VPS      (tailnet) ──┘        100.x.x.x       :48710 + PSK gate
```

Слои доступа к одному daemon'у:

| Слой | Механизм | Что защищает |
|---|---|---|
| L2/L3 | WireGuard-туннель tailnet | пакет извне tailnet не существует |
| Транспорт | TCP-listener только внутри tailnet / loopback / UDS | нет публичного порта |
| Приложение | PSK-handshake (`PskAuthRequest`, constant-time compare) | чужое устройство в tailnet не получит сервис |

---

## 1. Установка Tailscale

### Dell (домашняя машина, где живёт daemon)

```bash
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up
# → открой выданную ссылку, войди через Google/GitHub/SSO
tailscale ip -4          # → 100.x.y.z — адрес tailscale0
tailscale dns status | head   # MagicDNS-имя вида dell.tailXXXX.ts.net.
```

### Телефон (Android/iOS)

App Store / Google Play → «Tailscale» → войти в тот же аккаунт (тот же tailnet)
→ toggle On. Готово: телефон видит `100.x.y.z` Dell из любой сети.

### Проверка

```bash
# с телефона или другого устройства tailnet:
ping 100.x.y.z            # или ping dell.tailXXXX.ts.net
```

---

## 2. Запуск daemon с сетевым listener'ом

По умолчанию daemon слушает **только локальный UDS** (`HARBOR_LISTEN` не задан).
Чтобы его видели другие машины tailnet:

```bash
export HARBOR_LISTEN=tailscale0    # uds (default) | loopback | tailscale0 | all
export HARBOR_PORT=48710           # опционально, default 48710
harbor daemon start                # или просто: harbor --headless
```

Что произойдёт при старте:

1. Daemon забиндится **на адрес интерфейса tailscale0** (100.64/10). Если
   Tailscale не поднят — старт упадёт с явной ошибкой (не молча на eth0).
2. PSK: `~/.harbor/daemon.psk` — при первом запуске генерируется автоматически
   (128 бит), права 0600. Один файл = один ключ для всех клиентов.
3. В консоль печатается **pairing-блок**:

```
Remote pairing:
  harbor://dell.tailXXXX.ts.net:48710#kq3vZ8xQm2WnR7sT5yUb1a
  PSK file: /home/user/.harbor/daemon.psk
  ▛▚▚▚▚▚▚▚  ▚▚▜█▚▚ ...   ← QR с тем же кодом
```

> **Адрес в QR — приоритет tailscale > lan > loopback.** Если у машины есть и
> eth0 (192.168.x.x), и tailscale0 (100.x.x.x), в pairing-код попадёт
> tailnet-адрес: он работает и дома, и из другой страны. Локалку он тоже
> покрывает, а вот наоборот (eth0 вместо tailscale0) — нет.

---

## 3. Подключение клиента

### Телефон / другой ноутбук

1. Отсканируй QR с экрана Dell (или скопируй текст `harbor://…`).
2. Добавь хост в `~/.harbor/hosts.json` **на клиенте**:

```jsonc
{
  "dell": {
    "kind": "tailscale",
    "host": "dell.tailXXXX.ts.net",   // или "100.x.y.z"
    "port": 48710,
    "psk":   "kq3vZ8xQm2WnR7sT5yUb1a" // из QR / ~/.harbor/daemon.psk
  }
}
```

3. Проверь живость и подключайся:

```bash
harbor status --all     # параллельно опрашивает все хосты hosts.json
# dell           tails   dell.tailXXXX.ts.net:48710  alive
```

PSK обязателен: listener отвечает `PSK_REQUIRED` на любую команду до
handshake, неверный ключ = мгновенное закрытие соединения.

---

## 4. Безопасность — что уже сделано, что осознанно НЕ делано

Сделано (sprint 6):

- **UDS**: socket-файл chmod 0600 сразу после bind; stale-сокет удаляется
  только после probe-connect, мёртвого подтверждения; второй daemon не может
  «украсть» endpoint — bind падает с явной ошибкой.
- **TCP/tailscale listener**: PSK-handshake обязателен (`fail-closed`),
  сравнение constant-time; accept-loop с exponential backoff (EMFILE/ENFILE
  не крутит CPU).
- **Клиент переживает обрыв**: reconnect с backoff+jitter, переподписка событий,
  replay ≤ MAX_GAP=1000, dedup по sequence (мобильная сеть рвёт соединения
  постоянно — это норма, не авария).

Осознанно НЕ делаем (и почему):

- **Свою крипту/туннели** — Tailscale закрывает NAT-traversal, ротацию ключей,
  идентификацию устройств. Дублировать = получить дыру.
- **mDNS/DNS-SD discovery** — список хостов в `hosts.json` является источником
  истины. Предсказуемо, аудируемо, ноль лишнего трафика.
- **Публичный проброс порта** — listener `all` существует для отладки; для
  продакшена используй `tailscale0`.

---

## 5. Рецепты

### Несколько машин = mesh

На каждой машине свой daemon со своим PSK; на клиенте hosts.json перечисляет
все. Переключение сессий между машинами — killer feature session resume.

### Daemon как systemd-сервис (Dell)

```ini
# ~/.config/systemd/user/harbor-daemon.service
[Unit]
Description=Harbor IPC daemon (tailscale)
After=tailscaled.service
Wants=tailscaled.service

[Service]
Environment=HARBOR_LISTEN=tailscale0
ExecStart=/usr/local/bin/harbor --headless
Restart=on-failure

[Install]
WantedBy=default.target
```

```bash
systemctl --user enable --now harbor-daemon
```

### Firewall

Ничего открывать не надо: трафик идёт внутри tailscale0. firewalld может
вообще не знать про порт 48710.

### Диагностика

| Симптом | Причина | Что делать |
|---|---|---|
| `listenOn=tailscale0 but no Tailscale interface...` | `tailscale up` не выполнен / нет сети | поднять tailscale, перезапустить daemon |
| Клиент: `PSK_REQUIRED` | в hosts.json нет `"psk"` | добавить psk из QR |
| Клиент: `PSK_AUTH_FAILED` | опечатка в ключе | сверить с `~/.harbor/daemon.psk` |
| `status --all` показывает `down (timeout)` | устройство offline / не в tailnet | `tailscale status` на обеих сторонах |
| Порт занят другим daemon'ом | второй экземпляр | `HARBOR_PORT=` другой или `harbor daemon stop` |
