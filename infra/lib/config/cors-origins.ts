import { EnvironmentConfig } from "./environments";

/**
 * The browser origins that call the API and S3 directly: the web app and the
 * admin. Prod uses exact subdomains; dev and staging also allow localhost and
 * any extra origins of the environment.
 *
 * Shared by the HTTP API, the crop photo buckets and the uploads bucket (the
 * web app PUTs delivery photos to it and fetches them back, FRD-12), so a new
 * origin is added in one place.
 */
export function webCorsOrigins(envConfig: EnvironmentConfig): string[] {
  return envConfig.isProd
    ? ["https://app.weredaconnect.com", "https://admin.weredaconnect.com"]
    : [
        "http://localhost:3000",
        "http://localhost:4321",
        "http://localhost:5173",
        "http://localhost:8080",
        `https://${envConfig.domain.webappSubdomain}.${envConfig.domain.zoneName}`,
        `https://${envConfig.domain.adminSubdomain}.${envConfig.domain.zoneName}`,
        ...(envConfig.extraCorsOrigins ?? []),
      ];
}
