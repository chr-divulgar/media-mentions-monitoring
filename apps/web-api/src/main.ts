/**
 * This is not a production server yet!
 * This is only a minimal backend to get started.
 */

import { Logger } from '@nestjs/common';
import { NestFactory } from '@nestjs/core';

import { AppModule } from './app/app.module';

import compression from '@fastify/compress';
import fastifyStatic from '@fastify/static';
import { join } from 'path';
import {
  FastifyAdapter,
  NestFastifyApplication,
} from '@nestjs/platform-fastify';
import { CorsOptions } from '@nestjs/common/interfaces/external/cors-options.interface';
import { ConfigService } from '@nestjs/config';

async function bootstrap() {
  const fastifyAdapter = new FastifyAdapter({
    logger: true,
    bodyLimit: 10485760, // 10 MB
    connectionTimeout: 600000, // 10 minutes
  });
  const app = await NestFactory.create<NestFastifyApplication>(
    AppModule,
    fastifyAdapter,
  );

  const allowedOrigins = [
    'https://rpt-monitoreo.github.io',
    'http://localhost:4300',
    'http://localhost:4200',
  ];

  const corsOptions: CorsOptions = {
    origin: (origin, callback) => {
      if (!origin) return callback(null, true);
      if (allowedOrigins.includes(origin)) {
        return callback(null, true);
      }
      // Allow any subdomain of trycloudflare.com
      if (/^https:\/\/[a-zA-Z0-9-]+\.trycloudflare\.com$/.test(origin)) {
        return callback(null, true);
      }
      return callback(new Error('Not allowed by CORS'), false);
    },
    methods: 'GET,HEAD,PUT,PATCH,POST,DELETE,OPTIONS',
    credentials: true,
  };

  app.enableCors(corsOptions);
  app.register(compression);
  // Servir archivos estáticos del frontend
  app.register(fastifyStatic, {
    root: join(__dirname, '..', 'public'),
    prefix: '/',
    decorateReply: false,
  });
  const configService = app.get(ConfigService);
  const globalPrefix = '';
  app.setGlobalPrefix(globalPrefix);
  const port = configService.get<number>('BACK_PORT') || 3001;
  await app.listen(port, '0.0.0.0');

  Logger.log(
    `🚀 Application is running on: http://localhost:${port}/${globalPrefix}`,
  );
}

bootstrap();
