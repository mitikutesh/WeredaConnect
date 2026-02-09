# API Reference

## Backend API Documentation

Complete reference for WeredaConnect REST API.

## Audience
- Mobile developers
- Integration partners
- API consumers

## Base URL

- **Development**: `https://dev-api.weredaconnect.com`
- **Staging**: `https://staging-api.weredaconnect.com`
- **Production**: `https://api.weredaconnect.com`

---

## Authentication

### Bearer Token

All API requests require authentication via JWT bearer token.

```http
Authorization: Bearer <token>
```

### Get Token

```http
POST /auth/login
Content-Type: application/json

{
  "phoneNumber": "+251912345678",
  "password": "your-password"
}
```

**Response**:
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "refreshToken": "refresh-token-here",
  "expiresIn": 3600,
  "user": {
    "id": "user-123",
    "name": "John Doe",
    "phoneNumber": "+251912345678"
  }
}
```

---

## Reports API

### Create Report

```http
POST /reports
Authorization: Bearer <token>
Content-Type: application/json

{
  "title": "Broken road near school",
  "description": "Large pothole causing accidents",
  "category": "infrastructure",
  "location": {
    "latitude": 9.0320,
    "longitude": 38.7469,
    "address": "Bole, Addis Ababa"
  },
  "photos": ["photo-id-1", "photo-id-2"],
  "idempotencyKey": "unique-key-123"
}
```

**Response** (201 Created):
```json
{
  "id": "report-456",
  "title": "Broken road near school",
  "description": "Large pothole causing accidents",
  "category": "infrastructure",
  "status": "pending",
  "location": {
    "latitude": 9.0320,
    "longitude": 38.7469,
    "address": "Bole, Addis Ababa"
  },
  "photos": [
    {
      "id": "photo-id-1",
      "url": "https://cdn.weredaconnect.com/photos/photo-id-1.jpg",
      "thumbnail": "https://cdn.weredaconnect.com/photos/photo-id-1-thumb.jpg"
    }
  ],
  "createdBy": "user-123",
  "createdAt": "2026-02-09T10:30:00Z",
  "updatedAt": "2026-02-09T10:30:00Z",
  "version": 1,
  "upvotes": 0,
  "comments": 0
}
```

### Get Reports

```http
GET /reports?category=infrastructure&status=pending&limit=20&offset=0
Authorization: Bearer <token>
```

**Query Parameters**:
- `category` (optional): Filter by category
- `status` (optional): Filter by status
- `weredaId` (optional): Filter by wereda
- `limit` (optional): Results per page (default: 20, max: 100)
- `offset` (optional): Pagination offset (default: 0)
- `sortBy` (optional): `recent`, `nearby`, `upvotes` (default: `recent`)
- `latitude` (optional): For `nearby` sort
- `longitude` (optional): For `nearby` sort

**Response** (200 OK):
```json
{
  "reports": [
    {
      "id": "report-456",
      "title": "Broken road near school",
      "category": "infrastructure",
      "status": "pending",
      "location": {
        "latitude": 9.0320,
        "longitude": 38.7469
      },
      "photos": ["https://cdn.weredaconnect.com/photos/photo-id-1-thumb.jpg"],
      "createdAt": "2026-02-09T10:30:00Z",
      "upvotes": 5,
      "comments": 2
    }
  ],
  "total": 150,
  "limit": 20,
  "offset": 0,
  "hasMore": true
}
```

### Get Report by ID

```http
GET /reports/{reportId}
Authorization: Bearer <token>
```

**Response** (200 OK): Same as Create Report response

### Update Report

```http
PUT /reports/{reportId}
Authorization: Bearer <token>
Content-Type: application/json

{
  "title": "Updated title",
  "description": "Updated description",
  "version": 1
}
```

**Response** (200 OK): Updated report object

### Delete Report

```http
DELETE /reports/{reportId}
Authorization: Bearer <token>
```

**Response** (204 No Content)

### Upvote Report

```http
POST /reports/{reportId}/upvote
Authorization: Bearer <token>
```

**Response** (200 OK):
```json
{
  "upvotes": 6,
  "upvoted": true
}
```

---

## Sync API

### Sync Data

```http
POST /sync
Authorization: Bearer <token>
Content-Type: application/json

{
  "operations": [
    {
      "type": "create",
      "entityType": "report",
      "entityId": "local-id-123",
      "data": {
        "title": "New report",
        "description": "Created offline"
      },
      "version": 0,
      "idempotencyKey": "unique-key-456"
    },
    {
      "type": "update",
      "entityType": "report",
      "entityId": "report-789",
      "data": {
        "title": "Updated title"
      },
      "version": 2,
      "idempotencyKey": "unique-key-789"
    }
  ]
}
```

**Response** (200 OK):
```json
{
  "results": [
    {
      "entityId": "local-id-123",
      "status": "success",
      "serverId": "report-999",
      "version": 1
    },
    {
      "entityId": "report-789",
      "status": "conflict",
      "serverVersion": 3,
      "serverData": {
        "title": "Server version title",
        "version": 3
      }
    }
  ]
}
```

---

## Photos API

### Upload Photo

```http
POST /photos
Authorization: Bearer <token>
Content-Type: multipart/form-data

photo: <binary-data>
```

**Response** (201 Created):
```json
{
  "id": "photo-id-123",
  "url": "https://cdn.weredaconnect.com/photos/photo-id-123.jpg",
  "thumbnail": "https://cdn.weredaconnect.com/photos/photo-id-123-thumb.jpg",
  "size": 1024000,
  "width": 1920,
  "height": 1080
}
```

---

## Error Responses

### 400 Bad Request

```json
{
  "error": "validation_error",
  "message": "Invalid request data",
  "details": [
    {
      "field": "title",
      "message": "Title is required"
    }
  ]
}
```

### 401 Unauthorized

```json
{
  "error": "unauthorized",
  "message": "Invalid or expired token"
}
```

### 409 Conflict

```json
{
  "error": "conflict",
  "message": "Version conflict detected",
  "serverVersion": 3,
  "serverData": { }
}
```

### 500 Internal Server Error

```json
{
  "error": "internal_error",
  "message": "An unexpected error occurred",
  "requestId": "req-123-456"
}
```

---

## Rate Limiting

- **Limit**: 1000 requests per hour per user
- **Headers**:
  - `X-RateLimit-Limit`: 1000
  - `X-RateLimit-Remaining`: 950
  - `X-RateLimit-Reset`: 1675951200

**429 Too Many Requests**:
```json
{
  "error": "rate_limit_exceeded",
  "message": "Too many requests",
  "retryAfter": 3600
}
```

---

## Related Documentation

- **[Data Flow](../architecture/data-flow.md)** - Sync patterns
- **[Testing Guidelines](./testing-guidelines.md)** - API testing

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**API Version**: v1

