# Media Mentions Monitoring

## Documentación

- [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md) — arquitectura, flujos principales, endpoints y variables de entorno.

---

## Despliegue y ejecución

El backend (NestJS) sirve el frontend (React + Vite) como archivos estáticos. Solo necesitas desplegar el backend y acceder a la URL pública, donde estará disponible la web y las APIs.

### Pasos para producción

Cambiar el puerto a 3001

1. Construir el frontend:
   ```sh
   pnpm --filter ./apps/web-ui build
   ```
2. Copiar el build al backend:
   ```sh
   xcopy /E /I /Y apps\web-ui\dist apps\web-api\public
   ```
3. Build el backend:
   ```sh
   pnpm --filter ./apps/web-api build
   ```
4. Iniciar el Backend
   ```sh
   pnpm --filter ./apps/web-api start:prod
   ```

El backend servirá la web en la misma URL y puerto configurado.

---

## Estructura del monorepo

- apps/web-api: Backend NestJS
- apps/web-ui: Frontend React + Vite
- packages/: Paquetes compartidos

---

## Herramientas

- [TypeScript](https://www.typescriptlang.org/) para tipado estático
- [ESLint](https://eslint.org/) para linting
- [Prettier](https://prettier.io) for code formatting
