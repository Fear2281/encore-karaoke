# Encore

Сайт для музыкальных заявок в караоке-баре. Гости выбирают столик, заказывают треки из iTunes или чарта VK и видят общую очередь. В разделе «Своя музыка» можно указать название и исполнителя. В пульте диджей управляет заявками и таймером выступления.

## Развёртывание на сервере

На сервере нужны Git, Docker Engine и Docker Compose. С доменом и открытыми портами 80 и 443 Caddy настроит HTTPS.

```bash
git clone https://github.com/Fear2281/encore-karaoke.git
cd encore-karaoke
cp .env.example .env
chmod 600 .env
nano .env
docker compose config --quiet
docker compose up -d --build
docker compose ps
```

В `.env` задайте домен `DOMAIN` и пароль пульта `ENCORE_DJ_PASSWORD`. Токен VK необязателен. После запуска проверьте `https://ВАШ-ДОМЕН/api/health` и откройте пульт. Данные приложения хранятся в томе Docker `encore-data`.

[Инструкция по развёртыванию и резервному копированию](DEPLOY_RU.txt)

## Локальная разработка

```bash
dotnet restore Encore.csproj --locked-mode
dotnet run --project Encore.csproj
```

В режиме разработки демонстрационный пароль пульта — `encore-demo`. Для проверки выполните `dotnet build Encore.csproj`, затем `python scripts/smoke.py`.
