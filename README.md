# Controle Remoto LAN

App simples com interface para descobrir dispositivos comuns na rede local e enviar comandos ON/OFF quando suportado.

## Requisitos

- .NET SDK 8 (ou 7)
- Dispositivos Yeelight com LAN control ativado
- PC e dispositivos na mesma rede

## Como executar

```bash
dotnet run
```

## Uso rapido

- Aguarde a lista de dispositivos.
- Selecione o dispositivo na lista.
- Clique em ON ou OFF (quando suportado).
- Erros sao exibidos em uma caixa de dialogo.

## Configuracao

Edite o arquivo Config.json para controlar a varredura:

```json
{
	"scanOtherDevices": false
}
```

- `scanOtherDevices: false` = busca apenas Yeelight (mais rapido)
- `scanOtherDevices: true` = tenta identificar outros dispositivos (mais lento)

## Descoberta alternativa

Se o multicast nao encontrar nada, o app detecta as bases de IP locais e faz uma varredura rapida na porta 55443.

## Dispositivos suportados

- Yeelight (LAN)
- Tasmota (HTTP local)
- ESPHome (detecta, mas pode exigir chave para controle)
